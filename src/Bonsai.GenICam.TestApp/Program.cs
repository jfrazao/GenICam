using System;
using System.Reactive.Linq;
using System.Threading;
using Bonsai.GenICam;

namespace Bonsai.GenICam.TestApp
{
    class Program
    {
        static void Main(string[] args)
        {
            int targetIndex = args.Length > 0 ? int.Parse(args[0]) : 1;

            Console.WriteLine("=== Bonsai.GenICam Test ===");
            Console.WriteLine();

            // --- Enumerate ---
            Console.WriteLine("Enumerating GenICam devices...");
            DeviceInfo[]? devices = null;
            try { devices = new EnumerateDevices().Generate().Wait(); }
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
                    string xml = GenICamXmlExtractor.ExtractXml(null, i);
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
                var features = new ListFeatureValues { DeviceIndex = targetIndex }.Generate().Wait();
                foreach (var f in features)
                    Console.WriteLine($"  {f.Name} = {f.Value}");
            }
            catch (Exception ex) { Console.WriteLine($"  ListFeatureValues failed: {ex.Message}"); }
            Console.WriteLine();

            // --- Capture ---
            Console.WriteLine($"Capturing 5 frames from device {targetIndex}...");
            var capture = new GenICamCapture { DeviceIndex = targetIndex, NumBuffers = 4, FrameTimeoutMs = 5000 };

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
                bool completed = done.Wait(TimeSpan.FromSeconds(35));
                if (!completed)
                    Console.WriteLine("  Timed out — no frames or error in 35s (WaitForFrame kept returning null; AcquisitionStart likely failed).");
            }

            Console.WriteLine();
            Console.WriteLine("Test complete.");
        }
    }
}
