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
    /// Base class for typed GenICam feature read operators. Subclasses supply a
    /// <see cref="Convert"/> method that casts the raw <see cref="FeatureValue"/>
    /// to a concrete .NET type.
    /// </summary>
    public abstract class GetFeatureNodeBase<T> : Source<T>, IGenICamSource
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
        public string? FeatureCategory { get; set; }

        /// <summary>Gets or sets the name of the GenICam feature node to read (e.g. <c>ExposureTime</c>, <c>Gain</c>).</summary>
        [Description("Name of the GenICam feature node to read (e.g. ExposureTime, Gain).")]
        public string? FeatureName { get; set; }

        /// <summary>Gets or sets the interval between reads in milliseconds. Use 0 to emit a single value and complete.</summary>
        [Description("Interval between reads in milliseconds. Use 0 to emit a single value and complete.")]
        public double PeriodMs { get; set; } = 1000;

        /// <summary>Converts the raw <see cref="FeatureValue"/> returned by the NodeMap to the typed output <typeparamref name="T"/>.</summary>
        protected abstract T Convert(FeatureValue value);

        /// <inheritdoc/>
        public override IObservable<T> Generate()
        {
            if (string.IsNullOrWhiteSpace(FeatureName))
                throw new InvalidOperationException($"{GetType().Name}: FeatureName must be set.");
            return Observable.Using(() => OpenDevice(), ctx => BuildReadObservable(new NodeMap(ctx.Api, ctx.Port)));
        }

        private IObservable<T> BuildReadObservable(NodeMap map)
        {
            if (PeriodMs <= 0)
                return Observable.Return(Convert(map.Read(FeatureName!)));
            return Observable.Interval(TimeSpan.FromMilliseconds(PeriodMs))
                             .Select(_ => Convert(map.Read(FeatureName!)));
        }

        private GenICamDeviceContext OpenDevice()
        {
            var path   = string.IsNullOrWhiteSpace(ProducerPath) ? null : ProducerPath;
            var serial = string.IsNullOrWhiteSpace(SerialNumber)  ? null : SerialNumber;
            var model  = string.IsNullOrWhiteSpace(CameraModel)   ? null : CameraModel;
            if (serial != null || model != null)
            {
                var (api, system, iface, device) = GenTLLoader.FindAndOpenDeviceAcrossProducers(
                    serial, model, DeviceIndex, path, DeviceAccessFlags.ReadOnly);
                return new GenICamDeviceContext(api, system, iface, device);
            }
            var (a, localIndex) = GenTLLoader.ResolveAndLoad(path, DeviceIndex);
            var sys = new GenTLSystem(a);
            var (_, _, ifc, dev) = sys.FindAndOpenDevice(localIndex, DeviceAccessFlags.ReadOnly);
            return new GenICamDeviceContext(a, sys, ifc, dev);
        }
    }

    internal sealed class GenICamDeviceContext : IDisposable
    {
        internal readonly GenTLApi Api;
        internal readonly IntPtr Port;
        private readonly GenTLSystem _system;
        private readonly GenTLInterface _iface;
        private readonly GenTLDevice _device;

        internal GenICamDeviceContext(GenTLApi api, GenTLSystem system, GenTLInterface iface, GenTLDevice device)
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

    /// <summary>
    /// Reads a GenICam Integer feature node from a camera at a specified interval and emits each value as a <see cref="long"/>.
    /// </summary>
    [Description("Reads a GenICam Integer feature node from a camera at a specified interval.")]
    public class GetIntFeature : GetFeatureNodeBase<long>
    {
        /// <inheritdoc/>
        protected override long Convert(FeatureValue v) => (long)v.Value;
    }

    /// <summary>
    /// Reads a GenICam Float feature node from a camera at a specified interval and emits each value as a <see cref="double"/>.
    /// </summary>
    [Description("Reads a GenICam Float feature node from a camera at a specified interval.")]
    public class GetFloatFeature : GetFeatureNodeBase<double>
    {
        /// <inheritdoc/>
        protected override double Convert(FeatureValue v) => (double)v.Value;
    }

    /// <summary>
    /// Reads a GenICam Boolean feature node from a camera at a specified interval and emits each value as a <see cref="bool"/>.
    /// </summary>
    [Description("Reads a GenICam Boolean feature node from a camera at a specified interval.")]
    public class GetBoolFeature : GetFeatureNodeBase<bool>
    {
        /// <inheritdoc/>
        protected override bool Convert(FeatureValue v) => (bool)v.Value;
    }

    /// <summary>
    /// Reads a GenICam String or Enumeration feature node from a camera at a specified interval and emits each value as a <see cref="string"/>.
    /// </summary>
    [Description("Reads a GenICam String or Enumeration feature node from a camera at a specified interval.")]
    public class GetStringFeature : GetFeatureNodeBase<string>
    {
        /// <inheritdoc/>
        protected override string Convert(FeatureValue v) => v.Value?.ToString() ?? string.Empty;
    }
}
