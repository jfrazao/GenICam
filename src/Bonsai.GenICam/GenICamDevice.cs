using System;
using System.ComponentModel;
using System.Drawing.Design;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.GenICam.GenApi;
using Bonsai.GenICam.GenTL;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Single-owner GenICam device connection that routes <see cref="GenICamMessage"/> requests,
    /// serializing all reads and writes on a dedicated thread via an <see cref="EventLoopScheduler"/>.
    /// Read requests produce <see cref="GenICamMessageType.ReadResponse"/> messages;
    /// write requests produce <see cref="GenICamMessageType.WriteAck"/> messages.
    /// </summary>
    [Description("Routes GenICam messages through a single owned camera connection, serializing all reads and writes on a dedicated thread.")]
    public class GenICamDevice : Combinator<GenICamMessage, GenICamMessage>, IGenICamSource
    {
        NodeMap? IGenICamSource.LiveNodeMap => null;

        /// <summary>Gets or sets the path to a specific GenTL producer (.cti file). Leave empty to use the system search path.</summary>
        [Description("Path to a specific GenTL producer (.cti file). Leave empty to use the system search path.")]
        [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string? ProducerPath { get; set; }

        /// <summary>Gets or sets the zero-based index of the camera in the enumerated device list, or within the matching model group when <see cref="CameraModel"/> is set.</summary>
        [Description("Zero-based index of the camera in the enumerated device list, or within the matching model group when CameraModel is set.")]
        public int DeviceIndex { get; set; }

        /// <summary>Gets or sets the vendor+model string used to filter camera selection. Leave empty to select by <see cref="DeviceIndex"/> only.</summary>
        [Description("Optional: select camera by vendor+model string. Leave empty to select by DeviceIndex only.")]
        [Editor(typeof(CameraModelEditor), typeof(UITypeEditor))]
        public string? CameraModel { get; set; }

        /// <summary>Gets or sets the serial number used to identify the camera. When set, overrides <see cref="CameraModel"/> and <see cref="DeviceIndex"/>.</summary>
        [Description("Optional: select camera by serial number. When set, overrides CameraModel and DeviceIndex.")]
        [Editor(typeof(SerialNumberEditor), typeof(UITypeEditor))]
        public string? SerialNumber { get; set; }

        /// <inheritdoc/>
        public override IObservable<GenICamMessage> Process(IObservable<GenICamMessage> source)
        {
            return Observable.Using(
                OpenDevice,
                ctx => Observable.Using(
                    () => new EventLoopScheduler(),
                    scheduler =>
                    {
                        var map = new NodeMap(ctx.Api, ctx.Port);
                        return source
                            .ObserveOn(scheduler)
                            .Select(msg => Dispatch(msg, map));
                    }));
        }

        private static GenICamMessage Dispatch(GenICamMessage msg, NodeMap map)
        {
            switch (msg.Type)
            {
                case GenICamMessageType.WriteRequest:
                    map.Write(msg.FeatureName, msg.Payload ?? string.Empty);
                    return GenICamMessage.Ack(msg.FeatureName, msg.Payload ?? string.Empty);

                case GenICamMessageType.ReadRequest:
                    var fv = map.Read(msg.FeatureName);
                    return GenICamMessage.Response(msg.FeatureName, FormatValue(fv));

                default:
                    return msg;
            }
        }

        private static string FormatValue(FeatureValue v) => v.Value switch
        {
            double d => d.ToString(CultureInfo.InvariantCulture),
            long   l => l.ToString(CultureInfo.InvariantCulture),
            bool   b => b ? "True" : "False",
            _        => v.Value?.ToString() ?? string.Empty
        };

        private GenICamDeviceContext OpenDevice()
        {
            var path   = string.IsNullOrWhiteSpace(ProducerPath) ? null : ProducerPath;
            var serial = string.IsNullOrWhiteSpace(SerialNumber)  ? null : SerialNumber;
            var model  = string.IsNullOrWhiteSpace(CameraModel)   ? null : CameraModel;
            if (serial != null || model != null)
            {
                var (api, system, iface, device) = GenTLLoader.FindAndOpenDeviceAcrossProducers(
                    serial, model, DeviceIndex, path, DeviceAccessFlags.Control);
                return new GenICamDeviceContext(api, system, iface, device);
            }
            var (a, localIndex) = GenTLLoader.ResolveAndLoad(path, DeviceIndex);
            var sys = new GenTLSystem(a);
            var (_, _, ifc, dev) = sys.FindAndOpenDevice(localIndex);
            return new GenICamDeviceContext(a, sys, ifc, dev);
        }
    }
}
