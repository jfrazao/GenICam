using System;
using Bonsai.GenICam.GenApi;

namespace Bonsai.GenICam.GenTL
{
    /// <summary>
    /// An open GenTL device connection. Owns the producer (<see cref="GenTLApi"/>), system, interface,
    /// and device, and exposes the control <see cref="Port"/> plus a lazily-built <see cref="NodeMap"/>
    /// and the device's <see cref="DeviceInfo"/>. Disposing closes the whole stack in order
    /// (device → interface → system → producer).
    /// </summary>
    internal sealed class DeviceSession : IDisposable
    {
        internal GenTLApi Api { get; }
        internal IntPtr Port { get; }
        internal DeviceInfo Info { get; }

        private readonly GenTLSystem _system;
        private readonly GenTLInterface _iface;
        private readonly GenTLDevice _device;
        private NodeMap? _nodeMap;

        private DeviceSession(GenTLApi api, GenTLSystem system, GenTLInterface iface, GenTLDevice device, DeviceInfo info)
        {
            Api = api;
            _system = system;
            _iface = iface;
            _device = device;
            Info = info;
            Port = device.GetPort();
        }

        /// <summary>Lazily builds and caches the GenApi node map for this device's control port.</summary>
        internal NodeMap NodeMap => _nodeMap ??= new NodeMap(Api, Port);

        internal GenTLDataStream OpenDataStream() => _device.OpenDataStream();

        /// <summary>
        /// Opens a device using the standard selection priority: by <paramref name="serialNumber"/>,
        /// else by vendor+<paramref name="cameraModel"/> (with <paramref name="deviceIndex"/> selecting
        /// within the matching model group), else by global <paramref name="deviceIndex"/>. Empty/blank
        /// model and serial are treated as unset. The returned session owns and disposes the connection.
        /// </summary>
        internal static DeviceSession Open(
            string? producerPath, int deviceIndex, string? cameraModel, string? serialNumber,
            DeviceAccessFlags flags)
        {
            var path   = string.IsNullOrWhiteSpace(producerPath) ? null : producerPath;
            var serial = string.IsNullOrWhiteSpace(serialNumber) ? null : serialNumber;
            var model  = string.IsNullOrWhiteSpace(cameraModel)  ? null : cameraModel;

            if (serial != null || model != null)
            {
                var (api, system, iface, device, ifaceId, devId) =
                    GenTLLoader.FindAndOpenDeviceAcrossProducers(serial, model, deviceIndex, path, flags);
                return Build(api, system, iface, device, ifaceId, devId, deviceIndex);
            }

            var (a, localIndex) = GenTLLoader.ResolveAndLoad(path, deviceIndex);
            GenTLSystem? sys = null;
            try
            {
                sys = new GenTLSystem(a);
                var (ifaceId, devId, ifc, dev) = sys.FindAndOpenDevice(localIndex, flags);
                return Build(a, sys, ifc, dev, ifaceId, devId, deviceIndex);
            }
            catch
            {
                sys?.Dispose();
                a.Dispose();
                throw;
            }
        }

        private static DeviceSession Build(GenTLApi api, GenTLSystem system, GenTLInterface iface,
            GenTLDevice device, string ifaceId, string devId, int globalIndex)
        {
            var info = iface.GetDeviceInfo(devId);
            info.GlobalIndex  = globalIndex;
            info.InterfaceID  = ifaceId;
            info.ProducerPath = api.ProducerPath;
            return new DeviceSession(api, system, iface, device, info);
        }

        public void Dispose()
        {
            _device.Dispose();
            _iface.Dispose();
            _system.Dispose();
            Api.Dispose();
        }
    }
}
