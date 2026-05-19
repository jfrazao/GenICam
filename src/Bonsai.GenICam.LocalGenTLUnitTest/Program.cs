using System;
using System.Reactive.Linq;
using System.Threading;
using Bonsai.GenICam;

namespace Bonsai.GenICam.LocalGenTLUnitTest
{
    class Program
    {
        static void Main(string[] args)
        {
            // Usage: [producerPath] [deviceIndex]
            // producerPath — path to a .cti file; omit to scan GENICAM_GENTL64_PATH
            // deviceIndex  — defaults to 0 when a producer path is given, 1 otherwise
            string? producerPath = null;
            int targetIndex;
            if (args.Length > 0 && args[0].EndsWith(".cti", StringComparison.OrdinalIgnoreCase))
            {
                producerPath = args[0];
                targetIndex  = args.Length > 1 ? int.Parse(args[1]) : 0;
            }
            else
            {
                targetIndex = args.Length > 0 ? int.Parse(args[0]) : 1;
            }

            Console.WriteLine("=== Bonsai.GenICam Test ===");
            Console.WriteLine();

            // --- Enumerate ---
            Console.WriteLine("Enumerating GenICam devices...");
            Console.WriteLine(producerPath != null ? $"Producer: {producerPath}" : "Producer: (GENICAM_GENTL64_PATH)");
            DeviceInfo[]? devices = null;
            try { devices = new EnumerateDevices { ProducerPath = producerPath }.Generate().Wait(); }
            catch (Exception ex) { Console.WriteLine($"Enumeration failed: {ex.Message}"); Environment.Exit(1); return; }

            Console.WriteLine($"Found {devices.Length} device(s):");
            foreach (var d in devices)
                Console.WriteLine($"  [{d.GlobalIndex}] {d.Vendor} {d.Model} s/n={d.SerialNumber}");
            Console.WriteLine();

            if (targetIndex >= devices.Length)
            {
                Console.WriteLine($"No device at index {targetIndex}.");
                Environment.Exit(1); return;
            }

            Console.WriteLine($"Testing device [{targetIndex}]: {devices[targetIndex].Vendor} {devices[targetIndex].Model}");
            Console.WriteLine();

            // --- Extract XML from all cameras ---
            Console.WriteLine("=== Extracting GenICam XML from all cameras ===");
            for (int i = 0; i < devices.Length; i++)
            {
                Console.WriteLine();
                Console.WriteLine($"--- Camera {i}: {devices[i].Vendor} {devices[i].Model} (S/N: {devices[i].SerialNumber}) ---");
                try
                {
                    string xml = GenICamXmlExtractor.ExtractXml(producerPath, i);
                    Console.WriteLine($"XML length: {xml.Length} bytes");
                    
                    // Save to file
                    string outputDir = System.IO.Path.Combine(AppContext.BaseDirectory, "example-camera-xml");
                    System.IO.Directory.CreateDirectory(outputDir);
                    string filename = System.IO.Path.Combine(outputDir, $"camera_{i}_{devices[i].Model.Replace(" ", "_")}.xml");
                    System.IO.File.WriteAllText(filename, xml);
                    Console.WriteLine($"Saved to: {filename}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to extract XML: {ex.Message}");
                }
            }
            Console.WriteLine();
            Console.WriteLine();

            // --- List ALL readable features for target device ---
            Console.WriteLine($"All readable features of device {targetIndex}:");
            try
            {
                var features = new ListFeatureValues { ProducerPath = producerPath, DeviceIndex = targetIndex }.Generate().Wait();
                foreach (var f in features)
                    Console.WriteLine($"  {f.Name} = {f.Value}");
            }
            catch (Exception ex) { Console.WriteLine($"  ListFeatureValues failed: {ex.Message}"); }
            Console.WriteLine();

            // --- Write/Readback round-trip test ---
            Console.WriteLine("=== Write/Readback round-trip test (ExposureTime, Gain) ===");
            try
            {
                var results = FeatureRoundTripTester.Run(producerPath, targetIndex, new[] { "ExposureTime", "Gain" });
                foreach (var r in results)
                {
                    Console.WriteLine($"  {r.Name}:");
                    Console.WriteLine($"    Kind={r.Kind}  Rep={r.Representation}  Unit={r.Unit ?? "(none)"}");
                    Console.WriteLine($"    Limits: min={r.LimitMin ?? "none"}  max={r.LimitMax ?? "none"}  step={r.LimitStep ?? "none"}");
                    Console.WriteLine($"    Before: {r.ValueBefore}");
                    Console.WriteLine($"    Written: {r.ValueWritten}");
                    Console.WriteLine($"    Readback: {r.ValueReadBack}");
                    Console.WriteLine($"    Error: {r.Error ?? "none"}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"  Round-trip test failed: {ex.Message}"); }
            Console.WriteLine();

            // --- Capture ---
            Console.WriteLine($"Capturing 5 frames from device {targetIndex}...");
            var capture = new GenICamCapture { ProducerPath = producerPath, DeviceIndex = targetIndex, NumBuffers = 4, FrameTimeoutMs = 5000 };

            int frameCount = 0;
            var done = new ManualResetEventSlim(false);

            using (capture.Generate()
                .Take(5)
                .Subscribe(
                    frame =>
                    {
                        frameCount++;
                        Console.WriteLine($"  Frame {frameCount}: {frame.Width}x{frame.Height}  depth={frame.Depth}  ch={frame.Channels}");
                    },
                    ex =>
                    {
                        Console.WriteLine($"  ERROR: {ex.GetType().Name}: {ex.Message}");
                        if (ex.InnerException != null)
                            Console.WriteLine($"  Inner: {ex.InnerException.Message}");
                        done.Set();
                    },
                    () =>
                    {
                        Console.WriteLine($"  Done — {frameCount} frame(s) received.");
                        done.Set();
                    }))
            {
                done.Wait();
            }

            Console.WriteLine();
            Console.WriteLine("Test complete.");
        }
    }
}
