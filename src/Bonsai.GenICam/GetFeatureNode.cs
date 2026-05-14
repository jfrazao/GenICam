using System;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.GenICam.GenApi;
using Bonsai.GenICam.GenTL;

namespace Bonsai.GenICam
{
    [Description("Reads a named GenICam feature node from a camera at a specified interval.")]
    public class GetFeatureNode : Source<FeatureValue>
    {
        [Description("Path to a specific GenTL producer (.cti file). Leave empty to use the system search path.")]
        [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string? ProducerPath { get; set; }

        [Description("Zero-based index of the camera in the enumerated device list.")]
        public int DeviceIndex { get; set; }

        [Description("Name of the GenICam feature node to read (e.g. ExposureTime, Gain).")]
        public string? FeatureName { get; set; }

        [Description("Interval between reads in milliseconds. Use 0 to emit a single value and complete.")]
        public double PeriodMs { get; set; } = 1000;

        public override IObservable<FeatureValue> Generate()
        {
            return Observable.Using(
                () => OpenDevice(),
                ctx =>
                {
                    var map = new NodeMap(ctx.Api, ctx.Port);
                    if (PeriodMs <= 0)
                        return Observable.Return(map.Read(FeatureName!));
                    return Observable.Interval(TimeSpan.FromMilliseconds(PeriodMs))
                        .Select(_ => map.Read(FeatureName!));
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
