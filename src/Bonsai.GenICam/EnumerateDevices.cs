using System;
using System.Collections.Generic;
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
            var results = new List<DeviceInfo>();
            int globalIndex = 0;

            IEnumerable<string> producers = string.IsNullOrWhiteSpace(ProducerPath)
                ? GenTLLoader.FindProducers()
                : new[] { ProducerPath! };

            lock (GenTLLoader.ScanLock)
            foreach (string ctiPath in producers)
            {
                try
                {
                    using (var api = GenTLLoader.Load(ctiPath))
                    using (var system = new GenTLSystem(api))
                    {
                        foreach (string ifaceId in system.GetInterfaceIDs())
                        {
                            using (var iface = system.OpenInterface(ifaceId))
                            {
                                foreach (string devId in iface.GetDeviceIDs())
                                {
                                    string TryGet(DeviceInfoCmd cmd)
                                    {
                                        try { return iface.GetDeviceInfoString(devId, cmd); }
                                        catch { return string.Empty; }
                                    }

                                    results.Add(new DeviceInfo
                                    {
                                        GlobalIndex = globalIndex++,
                                        ID = devId,
                                        InterfaceID = ifaceId,
                                        ProducerPath = api.ProducerPath,
                                        Vendor = TryGet(DeviceInfoCmd.Vendor),
                                        Model = TryGet(DeviceInfoCmd.Model),
                                        SerialNumber = TryGet(DeviceInfoCmd.SerialNumber),
                                        TLType = TryGet(DeviceInfoCmd.TLType),
                                        DisplayName = TryGet(DeviceInfoCmd.DisplayName)
                                    });
                                }
                            }
                        }
                    }
                }
                catch { /* producer failed to init or enumerate — skip it */ }
            }

            return results.ToArray();
        }
    }
}
