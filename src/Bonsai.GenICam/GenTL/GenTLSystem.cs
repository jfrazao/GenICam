using System;
using System.Collections.Generic;
using System.Text;

namespace Bonsai.GenICam.GenTL
{
    internal sealed class GenTLSystem : GenTLHandle
    {
        internal GenTLSystem(GenTLApi api) : base(api)
        {
            GenTLException.Check(_api.TLOpen(out _handle));
            _api.TLUpdateInterfaceList(_handle, out _, 1000);
        }

        internal IReadOnlyList<string> GetInterfaceIDs()
        {
            GenTLException.Check(_api.TLGetNumInterfaces(_handle, out uint count));
            var ids = new string[count];
            for (uint i = 0; i < count; i++)
            {
                uint idx = i;
                ids[i] = GenTLMarshal.FetchStringRef(delegate(byte[] buf, ref UIntPtr sz) {
                    return _api.TLGetInterfaceID(_handle, idx, buf, ref sz);
                });
            }
            return ids;
        }

        internal GenTLInterface OpenInterface(string id)
        {
            var idBytes = Encoding.ASCII.GetBytes(id + "\0");
            GenTLException.Check(_api.TLOpenInterface(_handle, idBytes, out IntPtr hIface));
            return new GenTLInterface(_api, hIface);
        }

        internal int CountDevices()
        {
            int count = 0;
            foreach (string ifaceId in GetInterfaceIDs())
            {
                using (var iface = OpenInterface(ifaceId))
                    count += iface.GetDeviceIDs().Count;
            }
            return count;
        }

        // Walks every interface (keeping each handle alive while its devices are inspected — closing and
        // reopening causes GC_ERR_INVALID_ID on producers like IDS peak that assign dynamic device IDs
        // per interface session) and opens the first device for which `match` returns true. Interfaces
        // that don't hold the match are disposed; on any error the open interface is disposed and the
        // error rethrown. `notFound` builds the exception thrown when nothing matches.
        private (string ifaceId, string devId, GenTLInterface iface, GenTLDevice device) FindAndOpenDevice(
            Func<GenTLInterface, string, bool> match, DeviceAccessFlags flags, Func<Exception> notFound)
        {
            foreach (string ifaceId in GetInterfaceIDs())
            {
                var iface = OpenInterface(ifaceId);
                try
                {
                    foreach (string devId in iface.GetDeviceIDs())
                    {
                        if (match(iface, devId))
                        {
                            var device = iface.OpenDevice(devId, flags);
                            return (ifaceId, devId, iface, device);
                        }
                    }
                }
                catch
                {
                    iface.Dispose();
                    throw;
                }
                iface.Dispose();
            }
            throw notFound();
        }

        internal (string ifaceId, string devId, GenTLInterface iface, GenTLDevice device) FindAndOpenDevice(
            int index, DeviceAccessFlags flags = DeviceAccessFlags.Control)
        {
            int current = 0;
            return FindAndOpenDevice(
                (iface, devId) => current++ == index,
                flags,
                () => new InvalidOperationException($"Device index {index} not found. Only {current} device(s) are visible."));
        }

        internal (string ifaceId, string devId, GenTLInterface iface, GenTLDevice device) FindAndOpenDeviceBySerial(
            string serialNumber, DeviceAccessFlags flags = DeviceAccessFlags.Control)
        {
            return FindAndOpenDevice(
                (iface, devId) =>
                {
                    string serial;
                    try { serial = iface.GetDeviceInfoString(devId, DeviceInfoCmd.SerialNumber); }
                    catch { serial = string.Empty; }
                    return string.Equals(serial, serialNumber, StringComparison.Ordinal);
                },
                flags,
                () => new InvalidOperationException($"No camera with serial number '{serialNumber}' found."));
        }

        internal (string ifaceId, string devId, GenTLInterface iface, GenTLDevice device) FindAndOpenDeviceByModel(
            string cameraModel, int index, DeviceAccessFlags flags = DeviceAccessFlags.Control)
        {
            int current = 0;
            return FindAndOpenDevice(
                (iface, devId) =>
                {
                    string TryGet(DeviceInfoCmd cmd)
                    { try { return iface.GetDeviceInfoString(devId, cmd); } catch { return string.Empty; } }
                    string combined = (TryGet(DeviceInfoCmd.Vendor) + " " + TryGet(DeviceInfoCmd.Model)).Trim();
                    string display = TryGet(DeviceInfoCmd.DisplayName);
                    bool matches =
                        string.Equals(combined, cameraModel, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(display,  cameraModel, StringComparison.OrdinalIgnoreCase);
                    return matches && current++ == index;
                },
                flags,
                () => new InvalidOperationException($"Camera '{cameraModel}' at index {index} not found ({current} matching device(s) visible)."));
        }

        protected override void CloseHandle() => _api.TLClose(_handle);
    }
}
