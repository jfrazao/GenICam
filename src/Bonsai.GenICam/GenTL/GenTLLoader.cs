using System;
using System.Collections.Generic;
using System.IO;

namespace Bonsai.GenICam.GenTL
{
    internal static class GenTLLoader
    {
        internal static readonly object ScanLock = new object();

        // Returns all .cti files found on GENICAM_GENTL64_PATH / GENICAM_GENTL32_PATH.
        // Reads Machine + User scopes and merges them to avoid missing entries that aren't
        // in the process-inherited value (e.g. set after Bonsai was launched).
        internal static IEnumerable<string> FindProducers()
        {
            string envVar = IntPtr.Size == 8 ? "GENICAM_GENTL64_PATH" : "GENICAM_GENTL32_PATH";

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new List<string>();
            foreach (var scope in new[] {
                EnvironmentVariableTarget.Process,
                EnvironmentVariableTarget.Machine,
                EnvironmentVariableTarget.User })
            {
                string val = Environment.GetEnvironmentVariable(envVar, scope);
                if (string.IsNullOrWhiteSpace(val)) continue;
                foreach (string entry in val.Split(';'))
                {
                    string d = entry.Trim().Trim('"');
                    if (d.Length > 0 && seen.Add(d))
                        dirs.Add(d);
                }
            }

            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string cti in Directory.GetFiles(dir, "*.cti"))
                    yield return cti;
            }
        }

        // Load and initialize a GenTL producer. If ctiPath is null, uses the first producer
        // found on the system search path.
        internal static GenTLApi Load(string? ctiPath = null)
        {
            if (ctiPath != null)
                return new GenTLApi(ctiPath);

            string envVar = IntPtr.Size == 8 ? "GENICAM_GENTL64_PATH" : "GENICAM_GENTL32_PATH";
            foreach (string path in FindProducers())
                return new GenTLApi(path);

            throw new InvalidOperationException(
                $"No GenTL producers found. Set {envVar} or specify a ProducerPath.");
        }

        // Loads the correct producer for the given device index and returns it already initialized,
        // avoiding a double-load race. When explicitProducerPath is set uses only that producer.
        // Caller owns the returned GenTLApi and must dispose it.
        // Serialized via _scanLock so concurrent workflow starts don't race on GCInitLib/GCCloseLib.
        internal static (GenTLApi api, int localIndex) ResolveAndLoad(string? explicitProducerPath, int globalDeviceIndex)
        {
            if (explicitProducerPath != null)
                return (Load(explicitProducerPath), globalDeviceIndex);

            lock (ScanLock)
            {
                int offset = 0;
                foreach (string ctiPath in FindProducers())
                {
                    GenTLApi? api = null;
                    try
                    {
                        api = new GenTLApi(ctiPath);
                        int count;
                        using (var system = new GenTLSystem(api))
                            count = system.CountDevices();

                        if (offset + count > globalDeviceIndex)
                            return (api, globalDeviceIndex - offset);

                        offset += count;
                        api.Dispose();
                        api = null;
                    }
                    catch
                    {
                        api?.Dispose();
                    }
                }

                string envVar = IntPtr.Size == 8 ? "GENICAM_GENTL64_PATH" : "GENICAM_GENTL32_PATH";
                throw new InvalidOperationException(
                    $"No GenTL device found at index {globalDeviceIndex}. Check {envVar} and camera connections.");
            }
        }
    }
}
