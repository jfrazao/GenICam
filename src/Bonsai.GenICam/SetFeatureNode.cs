using System;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.GenICam.GenApi;
using Bonsai.GenICam.GenTL;

namespace Bonsai.GenICam
{
    [Description("Writes a value to a named GenICam feature node each time an element arrives, then passes the element through.")]
    public class SetFeatureNode : Combinator
    {
        [Description("Path to a specific GenTL producer (.cti file). Leave empty to use the system search path.")]
        [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string? ProducerPath { get; set; }

        [Description("Zero-based index of the camera in the enumerated device list.")]
        public int DeviceIndex { get; set; }

        [Description("Name of the GenICam feature node to write (e.g. ExposureTime, Gain).")]
        public string? FeatureName { get; set; }

        [Description("Value to write. Strings are accepted for all node types and coerced at runtime.")]
        public string? Value { get; set; }

        public override IObservable<TSource> Process<TSource>(IObservable<TSource> source)
        {
            return Observable.Using(
                () => OpenDevice(),
                ctx =>
                {
                    var map = new NodeMap(ctx.Api, ctx.Port);
                    return source.Do(_ => map.Write(FeatureName!, Value ?? string.Empty));
                });
        }

        private DeviceContext OpenDevice()
        {
            var api = GenTLLoader.Load(string.IsNullOrWhiteSpace(ProducerPath) ? null : ProducerPath);
            var system = new GenTLSystem(api);
            var (_, _, iface, device) = system.FindAndOpenDevice(DeviceIndex);
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
