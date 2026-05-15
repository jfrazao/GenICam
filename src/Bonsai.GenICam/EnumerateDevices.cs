using System;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.GenICam.GenTL;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Enumerates all GenICam devices visible on the GenTL transport layer.
    /// </summary>
    [Description("Enumerates all GenICam devices visible on the GenTL transport layer.")]
    public class EnumerateDevices : Source<DeviceInfo[]>
    {
        /// <summary>Gets or sets the path to a specific GenTL producer (.cti file). Leave empty to use the system search path.</summary>
        [Description("Path to a specific GenTL producer (.cti file). Leave empty to use the system search path.")]
        [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string? ProducerPath { get; set; }

        /// <summary>Returns an observable that emits a single <see cref="DeviceInfo"/> array and completes.</summary>
        public override IObservable<DeviceInfo[]> Generate()
        {
            return Observable.Defer(() => Observable.Return(Enumerate()));
        }

        private DeviceInfo[] Enumerate()
        {
            var path = string.IsNullOrWhiteSpace(ProducerPath) ? null : ProducerPath;
            return GenTLLoader.EnumerateAllDeviceInfos(path);
        }
    }
}
