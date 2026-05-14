using System;
using System.Collections.Generic;
using System.Text;

namespace Bonsai.GenICam.GenTL
{
    internal sealed class GenTLSystem : IDisposable
    {
        private readonly GenTLApi _api;
        private IntPtr _handle;

        internal GenTLSystem(GenTLApi api)
        {
            _api = api;
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
                ids[i] = GenTLApi.FetchStringRef(delegate(byte[] buf, ref UIntPtr sz) {
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

        // Walks all interfaces to find the device at the given flat global index and opens it,
        // all while keeping the interface handle alive.  Using a separate FindDevice + OpenInterface
        // cycle (close then reopen) causes GC_ERR_INVALID_ID on producers such as IDS peak that
        // assign dynamic device IDs per interface session.
        internal (string ifaceId, string devId, GenTLInterface iface, GenTLDevice device) FindAndOpenDevice(
            int index, DeviceAccessFlags flags = DeviceAccessFlags.Control)
        {
            int current = 0;
            foreach (string ifaceId in GetInterfaceIDs())
            {
                var iface = OpenInterface(ifaceId);
                try
                {
                    var devIds = iface.GetDeviceIDs();
                    for (int i = 0; i < devIds.Count; i++)
                    {
                        if (current == index)
                        {
                            var device = iface.OpenDevice(devIds[i], flags);
                            return (ifaceId, devIds[i], iface, device);
                        }
                        current++;
                    }
                }
                catch
                {
                    iface.Dispose();
                    throw;
                }
                iface.Dispose();
            }
            throw new InvalidOperationException(
                $"Device index {index} not found. Only {current} device(s) are visible.");
        }

        internal IReadOnlyList<DeviceInfo> EnumerateDeviceInfos(string producerPath)
        {
            var result = new List<DeviceInfo>();
            int globalIndex = 0;
            foreach (string ifaceId in GetInterfaceIDs())
            {
                using (var iface = OpenInterface(ifaceId))
                {
                    foreach (string devId in iface.GetDeviceIDs())
                    {
                        string TryGet(DeviceInfoCmd cmd)
                        { try { return iface.GetDeviceInfoString(devId, cmd); } catch { return string.Empty; } }
                        result.Add(new DeviceInfo
                        {
                            GlobalIndex = globalIndex++,
                            ID = devId,
                            InterfaceID = ifaceId,
                            ProducerPath = producerPath,
                            Vendor = TryGet(DeviceInfoCmd.Vendor),
                            Model = TryGet(DeviceInfoCmd.Model),
                            SerialNumber = TryGet(DeviceInfoCmd.SerialNumber),
                            TLType = TryGet(DeviceInfoCmd.TLType),
                            DisplayName = TryGet(DeviceInfoCmd.DisplayName)
                        });
                    }
                }
            }
            return result;
        }

        internal (string ifaceId, string devId, GenTLInterface iface, GenTLDevice device) FindAndOpenDeviceBySerial(
            string serialNumber, DeviceAccessFlags flags = DeviceAccessFlags.Control)
        {
            foreach (string ifaceId in GetInterfaceIDs())
            {
                var iface = OpenInterface(ifaceId);
                try
                {
                    foreach (string devId in iface.GetDeviceIDs())
                    {
                        string serial;
                        try { serial = iface.GetDeviceInfoString(devId, DeviceInfoCmd.SerialNumber); }
                        catch { serial = string.Empty; }
                        if (string.Equals(serial, serialNumber, StringComparison.Ordinal))
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
            throw new InvalidOperationException($"No camera with serial number '{serialNumber}' found.");
        }

        internal (string ifaceId, string devId, GenTLInterface iface, GenTLDevice device) FindAndOpenDeviceByModel(
            string cameraModel, int index, DeviceAccessFlags flags = DeviceAccessFlags.Control)
        {
            int current = 0;
            foreach (string ifaceId in GetInterfaceIDs())
            {
                var iface = OpenInterface(ifaceId);
                try
                {
                    foreach (string devId in iface.GetDeviceIDs())
                    {
                        string TryGet(DeviceInfoCmd cmd)
                        { try { return iface.GetDeviceInfoString(devId, cmd); } catch { return string.Empty; } }
                        string combined = (TryGet(DeviceInfoCmd.Vendor) + " " + TryGet(DeviceInfoCmd.Model)).Trim();
                        string display = TryGet(DeviceInfoCmd.DisplayName);
                        bool matches =
                            string.Equals(combined, cameraModel, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(display,  cameraModel, StringComparison.OrdinalIgnoreCase);
                        if (matches)
                        {
                            if (current == index)
                            {
                                var device = iface.OpenDevice(devId, flags);
                                return (ifaceId, devId, iface, device);
                            }
                            current++;
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
            throw new InvalidOperationException(
                $"Camera '{cameraModel}' at index {index} not found ({current} matching device(s) visible).");
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                _api.TLClose(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}
