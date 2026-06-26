using System;
using System.Collections.Generic;
using System.Text;

namespace Bonsai.GenICam.GenTL
{
    internal sealed class GenTLInterface : GenTLHandle
    {
        internal GenTLInterface(GenTLApi api, IntPtr handle) : base(api)
        {
            _handle = handle;
            _api.IFUpdateDeviceList(_handle, out _, 1000);
        }

        internal IReadOnlyList<string> GetDeviceIDs()
        {
            GenTLException.Check(_api.IFGetNumDevices(_handle, out uint count));
            var ids = new string[count];
            for (uint i = 0; i < count; i++)
            {
                uint idx = i;
                ids[i] = GenTLMarshal.FetchStringRef(delegate(byte[] buf, ref UIntPtr sz) {
                    return _api.IFGetDeviceID(_handle, idx, buf, ref sz);
                });
            }
            return ids;
        }

        // Returns a string device info field using IFGetDeviceInfo (GenTL 1.3+).
        // Falls back to opening the device and using DevGetInfo if not available.
        internal string GetDeviceInfoString(string deviceId, DeviceInfoCmd cmd)
        {
            var idBytes = Encoding.ASCII.GetBytes(deviceId + "\0");

            if (_api.IFGetDeviceInfo != null)
            {
                return GenTLMarshal.FetchStringRef(delegate(byte[] buf, ref UIntPtr sz) {
                    return _api.IFGetDeviceInfo(_handle, idBytes, (uint)cmd, out _, buf, ref sz);
                });
            }

            // Fallback: open device, query, close
            GenTLException.Check(_api.IFOpenDevice(_handle, idBytes, (uint)DeviceAccessFlags.ReadOnly, out IntPtr hDev));
            try
            {
                return GenTLMarshal.FetchStringRef(delegate(byte[] buf, ref UIntPtr sz) {
                    return _api.DevGetInfo(hDev, (uint)cmd, out _, buf, ref sz);
                });
            }
            finally
            {
                _api.DevClose(hDev);
            }
        }

        // Builds a DeviceInfo from the interface-derivable fields for the given device ID. The caller
        // fills GlobalIndex / InterfaceID / ProducerPath, which the interface does not know.
        internal DeviceInfo GetDeviceInfo(string deviceId)
        {
            string TryGet(DeviceInfoCmd cmd)
            { try { return GetDeviceInfoString(deviceId, cmd); } catch { return string.Empty; } }
            return new DeviceInfo
            {
                ID           = deviceId,
                Vendor       = TryGet(DeviceInfoCmd.Vendor),
                Model        = TryGet(DeviceInfoCmd.Model),
                SerialNumber = TryGet(DeviceInfoCmd.SerialNumber),
                TLType       = TryGet(DeviceInfoCmd.TLType),
                DisplayName  = TryGet(DeviceInfoCmd.DisplayName),
            };
        }

        internal GenTLDevice OpenDevice(string deviceId, DeviceAccessFlags flags = DeviceAccessFlags.Control)
        {
            var idBytes = Encoding.ASCII.GetBytes(deviceId + "\0");
            GenTLException.Check(_api.IFOpenDevice(_handle, idBytes, (uint)flags, out IntPtr hDev));
            return new GenTLDevice(_api, hDev);
        }

        protected override void CloseHandle() => _api.IFClose(_handle);
    }
}
