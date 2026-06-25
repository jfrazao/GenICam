using System;
using System.IO;
using System.Linq;
using Bonsai.GenICam.GenApi;

namespace Bonsai.GenICam.LocalGenTLUnitTest
{
    // Offline regression check for issue #13: a rejected feature read/write must be returned as an
    // Error message, NOT thrown. In the live pipeline GenICamDevice converted a thrown dispatch
    // exception into OnError, which faulted the stream and ran disposal on the EventLoopScheduler
    // thread — the ObjectDisposedException race in #13. This exercises GenICamDevice.Dispatch directly
    // against an offline NodeMap (built from a saved camera XML, no hardware): a request for a feature
    // the device does not expose resolves to a KeyNotFoundException inside Dispatch, which must now
    // surface as a GenICamMessageType.Error message rather than propagating.
    static class DispatchTester
    {
        public static bool RunOffline(string xmlDir)
        {
            Console.WriteLine("=== Dispatch error-message test (offline, #13) ===");
            var file = Directory.Exists(xmlDir)
                ? Directory.GetFiles(xmlDir, "*.xml").OrderBy(f => f).FirstOrDefault()
                : null;
            if (file == null)
            {
                Console.WriteLine("  SKIP: no fixture XML found.");
                Console.WriteLine();
                return true;
            }

            var map = new NodeMap(File.ReadAllText(file));
            const string bogus = "NoSuchFeature_ZZZ";
            bool ok = true;

            try
            {
                var w = GenICamDevice.Dispatch(GenICamMessage.Write(bogus, "1"), map);
                bool pass = w.Type == GenICamMessageType.Error && w.FeatureName == bogus;
                Console.WriteLine($"  write rejected -> {w.Type} ({w.Payload}) : {(pass ? "PASS" : "FAIL")}");
                ok &= pass;

                var r = GenICamDevice.Dispatch(GenICamMessage.Read(bogus), map);
                pass = r.Type == GenICamMessageType.Error && r.FeatureName == bogus;
                Console.WriteLine($"  read rejected  -> {r.Type} ({r.Payload}) : {(pass ? "PASS" : "FAIL")}");
                ok &= pass;

                // Startup-override path shares TryWrite, so a rejected override must also yield an Error
                // message (previously swallowed silently by FeatureConfiguration.Apply).
                var cfg = new FeatureConfiguration();
                cfg.Overrides.Add(new FeatureOverride { Name = bogus, Value = "1" });
                var startup = cfg.Apply(map).ToList();
                pass = startup.Count == 1 && startup[0].Type == GenICamMessageType.Error && startup[0].FeatureName == bogus;
                Console.WriteLine($"  startup override rejected -> {(startup.Count == 1 ? startup[0].Type.ToString() : $"{startup.Count} msgs")} : {(pass ? "PASS" : "FAIL")}");
                ok &= pass;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL: Dispatch threw instead of returning an Error message: {ex.GetType().Name}: {ex.Message}");
                ok = false;
            }

            Console.WriteLine($"  result: {(ok ? "PASS" : "FAIL")}  (fixture: {Path.GetFileName(file)})");
            Console.WriteLine();
            return ok;
        }
    }
}
