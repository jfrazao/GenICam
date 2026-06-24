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
            // Usage: [producerPath.cti] [deviceIndex] [sn=<serial>]
            //   producerPath — path to a .cti file; omit to scan GENICAM_GENTL64_PATH
            //   deviceIndex  — global device index (default 1, or 0 when a producer path is given)
            //   sn=<serial>  — pin the GenICamDevice capture/chunk/message-bus steps to this serial.
            //                  Recommended when several GenTL producers are present: the global
            //                  device index can resolve to a different physical camera on each open,
            //                  so index-based selection is not stable across producers.
            string? producerPath = null;
            string? serialNumber = null;
            int? indexArg = null;
            foreach (var a in args)
            {
                if (a.EndsWith(".cti", StringComparison.OrdinalIgnoreCase)) producerPath = a;
                else if (a.StartsWith("sn=", StringComparison.OrdinalIgnoreCase)) serialNumber = a.Substring(3);
                else if (int.TryParse(a, out int idx)) indexArg = idx;
            }
            int targetIndex = indexArg ?? (producerPath != null ? 0 : 1);
            if (serialNumber != null) Console.WriteLine($"(pinning GenICamDevice steps to serial {serialNumber})");

            Console.WriteLine("=== Bonsai.GenICam Test ===");
            Console.WriteLine();

            // --- Chunk decode (offline) ---
            // Deterministic; runs with no camera attached. Reads the saved tested-camera XML
            // fixtures copied next to the executable and exercises the chunk-ID map + TryReadChunk.
            ChunkDataTester.RunOffline(System.IO.Path.Combine(AppContext.BaseDirectory, "testedCameraXml"));

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

                    // Save to file, pretty-printed. The raw producer XML is effectively one
                    // multi-megabyte line; indenting it makes the fixtures readable and lets diffs
                    // across firmware revisions show the actual node changes instead of one huge line.
                    string outputDir = System.IO.Path.Combine(AppContext.BaseDirectory, "testedCameraXml");
                    System.IO.Directory.CreateDirectory(outputDir);
                    string filename = System.IO.Path.Combine(outputDir, $"{devices[i].Model.Replace(" ", "_")}.xml");
                    SaveFormattedXml(filename, xml);
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

            // --- Shared connection: capture + chunk data + feature round-trip over ONE connection ---
            // Mirrors real Bonsai usage (one open connection reused for frames and feature I/O)
            // instead of opening a separate connection per step, which some producers (e.g. IDS uEye)
            // cannot release fast enough between opens.
            ChunkDataTester.RunShared(producerPath, targetIndex, serialNumber);

            Console.WriteLine("Test complete.");
        }

        // Writes XML indented (2 spaces, LF line endings, UTF-8 no BOM) so saved fixtures are
        // readable and diff cleanly. Falls back to the raw text if the XML cannot be parsed.
        static void SaveFormattedXml(string path, string xml)
        {
            try
            {
                var doc = System.Xml.Linq.XDocument.Parse(xml);
                var settings = new System.Xml.XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "  ",
                    NewLineChars = "\n",
                    Encoding = new System.Text.UTF8Encoding(false),
                };
                using (var w = System.Xml.XmlWriter.Create(path, settings))
                    doc.Save(w);
            }
            catch
            {
                System.IO.File.WriteAllText(path, xml);
            }
        }
    }
}
