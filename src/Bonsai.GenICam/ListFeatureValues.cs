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
                () => DeviceSession.Open(ProducerPath, DeviceIndex, null, null, DeviceAccessFlags.ReadOnly),
                ctx =>
                {
                    var features = new System.Collections.Generic.List<FeatureValue>(ctx.NodeMap.TryReadAll());
                    return Observable.Return(features.ToArray());
                });
        }
    }
}
