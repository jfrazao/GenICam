using System;
using System.ComponentModel;
using System.Drawing.Design;
using System.Globalization;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.GenICam.GenApi;
using Bonsai.GenICam.GenTL;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Base class for typed GenICam feature write operators. Subclasses supply a
    /// <see cref="Format"/> method that converts a typed upstream value to the
    /// string accepted by <see cref="NodeMap.Write"/>.
    /// </summary>
    public abstract class SetFeatureNodeBase<T> : Combinator<T, T>, IGenICamFeatureNode
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

        /// <summary>Gets or sets the GenICam category used to filter the <see cref="FeatureName"/> dropdown. Leave empty to browse all features.</summary>
        [Description("Optional: filter the feature list by category. Leave empty to browse all features.")]
        [Editor(typeof(FeatureCategoryEditor), typeof(UITypeEditor))]
        public string? FeatureCategory { get; set; }

        /// <summary>Gets or sets the name of the GenICam feature node to write (e.g. <c>ExposureTime</c>, <c>Gain</c>).</summary>
        [Description("Name of the GenICam feature node to write (e.g. ExposureTime, Gain).")]
        [Editor(typeof(FeatureNameEditor), typeof(UITypeEditor))]
        public string? FeatureName { get; set; }

        /// <summary>Formats the typed upstream value as the string accepted by <see cref="NodeMap.Write"/>.</summary>
        protected abstract string Format(T value);

        /// <inheritdoc/>
        public override IObservable<T> Process(IObservable<T> source)
        {
            var path   = string.IsNullOrWhiteSpace(ProducerPath) ? null : ProducerPath;
            var serial = string.IsNullOrWhiteSpace(SerialNumber)  ? null : SerialNumber;
            var model  = string.IsNullOrWhiteSpace(CameraModel)   ? null : CameraModel;
            var key    = NodeMapRegistry.MakeKey(serial, model, DeviceIndex, path);

            var shared = NodeMapRegistry.TryLookup(key);
            if (shared != null)
                return source.Do(v => shared.Write(FeatureName!, Format(v)));

            return Observable.Using(
                () => OpenDevice(),
                ctx =>
                {
                    var map = new NodeMap(ctx.Api, ctx.Port);
                    return source.Do(v => map.Write(FeatureName!, Format(v)));
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

    /// <summary>
    /// Writes a <see cref="long"/> value to a GenICam Integer feature node on each upstream element, then passes the element through.
    /// </summary>
    [Description("Writes a long value to a GenICam Integer feature node on each upstream element, then passes the element through.")]
    public class SetIntFeature : SetFeatureNodeBase<long>
    {
        /// <inheritdoc/>
        protected override string Format(long v) => v.ToString();
    }

    /// <summary>
    /// Writes a <see cref="double"/> value to a GenICam Float feature node on each upstream element, then passes the element through.
    /// </summary>
    [Description("Writes a double value to a GenICam Float feature node on each upstream element, then passes the element through.")]
    public class SetFloatFeature : SetFeatureNodeBase<double>
    {
        /// <inheritdoc/>
        protected override string Format(double v) => v.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Writes a <see cref="bool"/> value to a GenICam Boolean feature node on each upstream element, then passes the element through.
    /// </summary>
    [Description("Writes a bool value to a GenICam Boolean feature node on each upstream element, then passes the element through.")]
    public class SetBoolFeature : SetFeatureNodeBase<bool>
    {
        /// <inheritdoc/>
        protected override string Format(bool v) => v ? "True" : "False";
    }

    /// <summary>
    /// Writes a <see cref="string"/> value to a GenICam String or Enumeration feature node on each upstream element, then passes the element through.
    /// </summary>
    [Description("Writes a string value to a GenICam String or Enumeration feature node on each upstream element, then passes the element through.")]
    public class SetStringFeature : SetFeatureNodeBase<string>
    {
        /// <inheritdoc/>
        protected override string Format(string v) => v;
    }
}
