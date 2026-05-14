using System;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.GenICam.GenTL;

namespace Bonsai.GenICam
{
    [Description("Enumerates all GenICam devices visible on the GenTL transport layer.")]
    public class EnumerateDevices : Source<DeviceInfo[]>
    {
        [Description("Path to a specific GenTL producer (.cti file). Leave empty to use the system search path.")]
        [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string? ProducerPath { get; set; }

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
