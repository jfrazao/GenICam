using System;
using System.Collections.Generic;
using Bonsai.GenICam;
using Bonsai.GenICam.GenApi;
using Bonsai.GenICam.GenTL;

namespace Bonsai.GenICam.TestApp
{
    class FeatureRoundTripResult
    {
        public string Name { get; set; } = string.Empty;
        public string? Kind { get; set; }
        public string? Representation { get; set; }
        public string? Unit { get; set; }
        public string? LimitMin { get; set; }
        public string? LimitMax { get; set; }
        public string? LimitStep { get; set; }
        public string? ValueBefore { get; set; }
        public string? ValueWritten { get; set; }
        public string? ValueReadBack { get; set; }
        public string? Error { get; set; }
    }

    static class FeatureRoundTripTester
    {
        public static List<FeatureRoundTripResult> Run(string? producerPath, int deviceIndex, string[] featureNames)
        {
            var results = new List<FeatureRoundTripResult>();

            var (api, localIndex) = GenTLLoader.ResolveAndLoad(
                string.IsNullOrWhiteSpace(producerPath) ? null : producerPath, deviceIndex);
            try
            {
                var system = new GenTLSystem(api);
                var (_, _, iface, device) = system.FindAndOpenDevice(localIndex, DeviceAccessFlags.Control);
                try
                {
                    var map = new NodeMap(api, device.GetPort());

                    foreach (var name in featureNames)
                    {
                        var result = new FeatureRoundTripResult { Name = name };
                        results.Add(result);
                        try
                        {
                            var kind   = map.GetNodeKind(name);
                            var limits = map.GetNodeLimits(name);
                            var unit   = map.GetNodeUnit(name);
                            var rep    = map.GetNodeRepresentation(name);

                            result.Kind           = kind.ToString();
                            result.Representation = rep.ToString();
                            result.Unit           = unit;
                            result.LimitMin       = limits.min?.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            result.LimitMax       = limits.max?.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            result.LimitStep      = limits.step?.ToString(System.Globalization.CultureInfo.InvariantCulture);

                            var before = map.Read(name);
                            result.ValueBefore = before.Value?.ToString();

                            string testVal = "20000";
                            result.ValueWritten = testVal;
                            map.Write(name, testVal);

                            var after = map.Read(name);
                            result.ValueReadBack = after.Value?.ToString();

                            if (result.ValueBefore != null)
                                map.Write(name, result.ValueBefore);
                        }
                        catch (Exception ex) { result.Error = ex.Message; }
                    }
                }
                finally
                {
                    device.Dispose();
                    iface.Dispose();
                    system.Dispose();
                }
            }
            finally { api.Dispose(); }

            return results;
        }
    }
}
