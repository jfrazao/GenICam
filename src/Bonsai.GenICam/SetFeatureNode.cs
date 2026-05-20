using System;
using System.ComponentModel;
using System.Drawing.Design;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.GenICam.GenApi;
using Bonsai.GenICam.GenTL;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Writes a value to a named GenICam feature node each time an element arrives, then passes the element through.
    /// </summary>
    [Description("Writes a value to a named GenICam feature node each time an element arrives, then passes the element through.")]
    public class SetFeatureNode : Combinator, IGenICamSource
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

        /// <summary>Gets or sets the name of a <see cref="GenICamCapture"/> whose connection this operator should share. When set, the camera identity properties are ignored.</summary>
        [Description("Optional: name of a GenICamCapture to share its connection with. When set, overrides ProducerPath/DeviceIndex/CameraModel/SerialNumber.")]
        [Editor(typeof(ConnectionNameEditor), typeof(UITypeEditor))]
        public string? Connection { get; set; }

        /// <summary>Gets or sets the GenICam category used to filter the <see cref="FeatureName"/> dropdown. Leave empty to browse all features.</summary>
        [Description("Optional: filter the feature list by category. Leave empty to browse all features.")]
        [Editor(typeof(FeatureCategoryEditor), typeof(UITypeEditor))]
        public string? FeatureCategory { get; set; }

        /// <summary>Gets or sets the name of the GenICam feature node to write (e.g. <c>ExposureTime</c>, <c>Gain</c>).</summary>
        [Description("Name of the GenICam feature node to write (e.g. ExposureTime, Gain).")]
        [Editor(typeof(FeatureNameEditor), typeof(UITypeEditor))]
        public string? FeatureName { get; set; }

        /// <summary>Gets or sets the value to write. Strings are accepted for all node types and coerced at runtime.</summary>
        [Description("Value to write. Strings are accepted for all node types and coerced at runtime.")]
        public string? Value { get; set; }

        /// <summary>Writes <see cref="Value"/> to the named feature on each element and passes each element through unchanged.</summary>
        public override IObservable<TSource> Process<TSource>(IObservable<TSource> source)
        {
            if (string.IsNullOrWhiteSpace(FeatureName))
                throw new InvalidOperationException("SetFeatureNode: FeatureName must be set.");
            if (Value == null)
                throw new InvalidOperationException("SetFeatureNode: Value must be set. To write an upstream FeatureValue directly, use SetFeatureValue instead.");

            string valueStr = Value;
            string? sourceName = this.Connection;
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                return Observable.Using(
                    () => GenICamConnectionManager.Acquire(sourceName)
                          ?? throw new InvalidOperationException($"GenICamCapture named '{sourceName}' did not publish a connection within the timeout."),
                    conn => source.Do(_ => conn.NodeMap.Write(FeatureName!, valueStr)));
            }

            return Observable.Using(
                () => OpenDevice(),
                ctx =>
                {
                    var map = new NodeMap(ctx.Api, ctx.Port);
                    return source.Do(_ => map.Write(FeatureName!, valueStr));
                });
        }

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
