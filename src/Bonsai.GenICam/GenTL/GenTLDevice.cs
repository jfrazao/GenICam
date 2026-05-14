using System;
using System.Text;

namespace Bonsai.GenICam.GenTL
{
    internal sealed class GenTLDevice : IDisposable
    {
        private readonly GenTLApi _api;
        private IntPtr _handle;

        internal GenTLDevice(GenTLApi api, IntPtr handle)
        {
            _api = api;
            _handle = handle;
        }

        internal IntPtr GetPort()
        {
            GenTLException.Check(_api.DevGetPort(_handle, out IntPtr hPort));
            return hPort;
        }

        internal string GetInfoString(DeviceInfoCmd cmd)
        {
            return GenTLApi.FetchStringRef(delegate(byte[] buf, ref UIntPtr sz) {
                return _api.DevGetInfo(_handle, (uint)cmd, out _, buf, ref sz);
            });
        }

        internal GenTLDataStream OpenDataStream()
        {
            GenTLException.Check(_api.DevGetNumDataStreams(_handle, out uint count));
            if (count == 0)
                throw new InvalidOperationException("Device exposes no data streams.");

            var streamId = GenTLApi.FetchStringRef(delegate(byte[] buf, ref UIntPtr sz) {
                return _api.DevGetDataStreamID(_handle, 0, buf, ref sz);
            });

            var idBytes = Encoding.ASCII.GetBytes(streamId + "\0");
            GenTLException.Check(_api.DevOpenDataStream(_handle, idBytes, out IntPtr hDS));
            return new GenTLDataStream(_api, hDS);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                _api.DevClose(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}
