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
