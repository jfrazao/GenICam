using System;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.GenICam.GenApi;
using Bonsai.GenICam.GenTL;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Reads all accessible feature nodes from a GenICam device and emits them as an array.
    /// </summary>
    [Description("Reads all accessible feature nodes from a GenICam device and emits them as an array.")]
    public class ListFeatureValues : Source<FeatureValue[]>
    {
        /// <summary>Gets or sets the path to a specific GenTL producer (.cti file). Leave empty to use the system search path.</summary>
        [Description("Path to a specific GenTL producer (.cti file). Leave empty to use the system search path.")]
        [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string? ProducerPath { get; set; }

        /// <summary>Gets or sets the zero-based index of the camera in the enumerated device list.</summary>
        [Description("Zero-based index of the camera in the enumerated device list.")]
        public int DeviceIndex { get; set; }

        /// <summary>Returns an observable that emits a single <see cref="FeatureValue"/> array snapshot and completes.</summary>
        public override IObservable<FeatureValue[]> Generate()
        {
            return Observable.Using(
                () => OpenDevice(),
                ctx =>
                {
                    var map = new NodeMap(ctx.Api, ctx.Port);
                    var features = new System.Collections.Generic.List<FeatureValue>(map.TryReadAll());
                    return Observable.Return(features.ToArray());
                });
        }

        private DeviceContext OpenDevice()
        {
            var (api, localIndex) = GenTLLoader.ResolveAndLoad(
                string.IsNullOrWhiteSpace(ProducerPath) ? null : ProducerPath, DeviceIndex);
            var system = new GenTLSystem(api);
            var (_, _, iface, device) = system.FindAndOpenDevice(localIndex, DeviceAccessFlags.ReadOnly);
            return new DeviceContext(api, system, iface, device);
        }

        private sealed class DeviceContext : IDisposable
        {
            internal readonly GenTLApi Api;
            internal readonly IntPtr Port;
            private readonly GenTLSystem _system;
            private readonly GenTLInterface _iface;
            private readonly GenTLDevice _device;

            internal DeviceContext(GenTLApi api, GenTLSystem system, GenTLInterface iface, GenTLDevice device)
            {
                Api = api;
                _system = system;
                _iface = iface;
                _device = device;
                Port = device.GetPort();
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
}
