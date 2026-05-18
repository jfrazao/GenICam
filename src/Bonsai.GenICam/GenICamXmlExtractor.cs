using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Bonsai.GenICam.GenTL;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Utility to extract and display GenICam XML from camera devices.
    /// </summary>
    public static class GenICamXmlExtractor
    {
        /// <summary>Connects to the camera at <paramref name="deviceIndex"/> and returns its raw GenICam XML string.</summary>
        public static string ExtractXml(string? producerPath, int deviceIndex)
        {
            var (api, localIndex) = GenTLLoader.ResolveAndLoad(
                string.IsNullOrWhiteSpace(producerPath) ? null : producerPath, deviceIndex);

            try
            {
                var system = new GenTLSystem(api);

                // Try read-only first, then control, then exclusive access
                GenTLInterface iface;
                GenTLDevice device;
                try
                {
                    (_, _, iface, device) = system.FindAndOpenDevice(localIndex, DeviceAccessFlags.ReadOnly);
                }
                catch
                {
                    try { (_, _, iface, device) = system.FindAndOpenDevice(localIndex, DeviceAccessFlags.Control); }
                    catch { (_, _, iface, device) = system.FindAndOpenDevice(localIndex, DeviceAccessFlags.Exclusive); }
                }

                try
                {
                    return FetchXml(api, device.GetPort());
                }
                finally
                {
                    device.Dispose();
                    iface.Dispose();
                    system.Dispose();
                }
            }
            finally
            {
                api.Dispose();
            }
        }

        internal static string FetchXml(GenTLApi api, IntPtr port)
        {
            string url = GenTLApi.FetchStringRef(delegate (byte[] buf, ref UIntPtr sz)
            {
                return api.GCGetPortURL(port, buf, ref sz);
            });

            if (url.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = url.Substring(6).Split(';');
                if (parts.Length < 3)
                    throw new InvalidOperationException($"Unexpected local: URL format: {url}");

                ulong address = ParseHex(parts[1]);
                int length = (int)ParseHex(parts[2]);

                var bytes = new byte[length];
                var size = new UIntPtr((uint)length);
                GenTLException.Check(api.GCReadPort(port, address, bytes, ref size));

                return DecompressOrDecode(bytes, parts[0], url);
            }
            else if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return File.ReadAllText(new Uri(url).LocalPath, Encoding.UTF8);
            }
            else
            {
                throw new NotSupportedException($"Unsupported GenICam XML URL scheme: {url}");
            }
        }

        internal static ulong ParseHex(string s)
        {
            s = s.Trim();
            int q = s.IndexOf('?');
            if (q >= 0) s = s.Substring(0, q);
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("&H", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);
            return Convert.ToUInt64(s, 16);
        }

        internal static string DecompressOrDecode(byte[] bytes, string filename, string url)
        {
            if ((bytes.Length >= 2 && bytes[0] == 0x50 && bytes[1] == 0x4B) ||
                filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using (var ms = new MemoryStream(bytes))
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    if (archive.Entries.Count == 0)
                        throw new InvalidOperationException($"GenICam XML ZIP at '{url}' contains no entries.");
                    using (var reader = new StreamReader(archive.Entries[0].Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false))
                        return reader.ReadToEnd();
                }
            }

            if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using (var ms = new MemoryStream(bytes))
                using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                using (var reader = new StreamReader(gz, Encoding.UTF8))
                    return reader.ReadToEnd();
            }

            int offset = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset).TrimEnd('\0');
        }
    }
}
