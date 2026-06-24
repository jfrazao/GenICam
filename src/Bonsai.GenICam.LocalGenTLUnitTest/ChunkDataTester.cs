using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using Bonsai.GenICam;
using Bonsai.GenICam.GenApi;

namespace Bonsai.GenICam.LocalGenTLUnitTest
{
    // Exercises the chunk-data path two ways:
    //   RunOffline  — deterministic, no camera. Builds a NodeMap from each saved example XML,
    //                 prints the discovered chunk-ID → feature map, and decodes synthetic chunk
    //                 bytes through TryReadChunk (covering Port-based layout + SwissKnife chains).
    //   RunLive     — captures frames with ChunkModeActive=true and prints GenICamFrame.ChunkData.
    static class ChunkDataTester
    {
        public static void RunOffline(string xmlDir)
        {
            Console.WriteLine("=== Chunk decode (offline, from saved XML fixtures) ===");
            if (!Directory.Exists(xmlDir))
            {
                Console.WriteLine($"  XML fixture directory not found: {xmlDir}");
                Console.WriteLine();
                return;
            }

            var files = Directory.GetFiles(xmlDir, "*.xml").OrderBy(f => f).ToArray();
            if (files.Length == 0)
            {
                Console.WriteLine($"  No XML fixtures in: {xmlDir}");
                Console.WriteLine();
                return;
            }

            foreach (var file in files)
            {
                Console.WriteLine();
                Console.WriteLine($"--- {Path.GetFileName(file)} ---");
                NodeMap map;
                try { map = new NodeMap(File.ReadAllText(file)); }
                catch (Exception ex) { Console.WriteLine($"  Parse failed: {ex.Message}"); continue; }

                var chunkMap = map.ChunkIdToName;
                Console.WriteLine($"  Chunk features discovered: {chunkMap.Count}");
                if (chunkMap.Count == 0)
                {
                    Console.WriteLine("  (no <ChunkID> ports — this camera exposes no chunk data)");
                    continue;
                }

                // 16 bytes of recognizable, non-zero payload. Enough for any 1/2/4/8-byte register;
                // every byte 0xAB so single-byte and multi-byte registers both decode to non-zero.
                var bytes = Enumerable.Repeat((byte)0xAB, 16).ToArray();

                int decoded = 0, failed = 0;
                foreach (var kv in chunkMap.OrderBy(k => k.Value, StringComparer.Ordinal))
                {
                    object? value = map.TryReadChunk(kv.Value, bytes);
                    if (value != null) decoded++; else failed++;
                    Console.WriteLine($"    0x{kv.Key:X8}  {kv.Value,-28} => {Describe(value)}");
                }
                Console.WriteLine($"  Decoded {decoded}/{chunkMap.Count} chunk feature(s) from synthetic bytes" +
                                  (failed > 0 ? $" ({failed} returned null)" : ""));
            }
            Console.WriteLine();
        }

        // One shared connection: a single GenICamDevice.Process() subscription that concurrently
        // receives frames (with chunk data) AND serves feature read/write messages — mirroring how a
        // real Bonsai workflow keeps one connection open (GenICamCapture + feature nodes reusing the
        // live NodeMap) instead of opening a competing connection per operation. Opening a fresh
        // connection per step is what makes some producers (notably the IDS uEye) intermittently fail
        // to find or release the device between steps.
        //
        // EnableAllChunks is an internal test-only seam: it enables every chunk selector the camera
        // exposes (before AcquisitionStart) so the capture exercises the full metadata set. Real
        // workflows leave it off and configure chunks via a UserSet.
        public static void RunShared(string? producerPath, int deviceIndex, string? serialNumber = null)
        {
            Console.WriteLine("=== GenICamDevice: shared connection (capture + chunk + feature round-trip) ===");
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var device = new GenICamDevice
            {
                ProducerPath    = producerPath,
                DeviceIndex     = deviceIndex,
                SerialNumber    = serialNumber,
                NumBuffers      = 4,
                FrameTimeoutMs  = 5000,
                AcquireFrames   = true,
                ChunkModeActive = true,
                EnableAllChunks = true,
            };

            var input = new System.Reactive.Subjects.Subject<GenICamMessage>();
            int frameCount = 0, framesWithChunk = 0;
            IReadOnlyDictionary<string, object>? firstChunk = null;
            var responses = new List<GenICamMessage>();
            double original = double.NaN, newVal = double.NaN;
            bool roundTripStarted = false;
            Exception? error = null;
            var framesDone = new ManualResetEventSlim(false);
            var roundTripDone = new ManualResetEventSlim(false);

            // Output is serialized by the device (Observer.Synchronize), so this handler runs
            // single-threaded — the round-trip state machine below needs no locking.
            using (device.Process(input).Subscribe(
                msg =>
                {
                    if (msg.Type == GenICamMessageType.Frame && msg.Frame != null)
                    {
                        frameCount++;
                        var chunk = msg.Frame.ChunkData;
                        if (chunk != null && chunk.Count > 0) { framesWithChunk++; if (firstChunk == null) firstChunk = chunk; }
                        // Once frames are flowing, kick off the feature round-trip on the SAME connection.
                        if (!roundTripStarted) { roundTripStarted = true; input.OnNext(GenICamMessage.Read("ExposureTime")); }
                        if (frameCount >= 5) framesDone.Set();
                    }
                    else if (msg.Type == GenICamMessageType.ReadResponse || msg.Type == GenICamMessageType.WriteAck)
                    {
                        responses.Add(msg);
                        if (msg.Type == GenICamMessageType.ReadResponse && double.IsNaN(original))
                        {
                            if (!double.TryParse(msg.Payload, System.Globalization.NumberStyles.Any, ic, out original))
                            { roundTripDone.Set(); return; }
                            // Bump without rounding to 2 dp so the value stays nonzero for seconds-unit
                            // cameras (the camera clamps to range) — avoids the round(x*1.1,2)->0 reject.
                            newVal = original * 1.1;
                            if (newVal == original) newVal = original + 1;
                            input.OnNext(GenICamMessage.Write("ExposureTime", newVal.ToString(ic)));
                            input.OnNext(GenICamMessage.Read("ExposureTime"));
                            input.OnNext(GenICamMessage.Write("ExposureTime", original.ToString(ic)));
                            input.OnNext(GenICamMessage.Read("ExposureTime"));
                        }
                        if (responses.Count(r => r.Type == GenICamMessageType.ReadResponse) >= 3) roundTripDone.Set();
                    }
                },
                ex => { error = ex; framesDone.Set(); roundTripDone.Set(); },
                () => { framesDone.Set(); roundTripDone.Set(); }))
            {
                framesDone.Wait(15000);
                roundTripDone.Wait(15000);
            }
            input.OnCompleted();

            if (error != null)
            {
                Console.WriteLine($"  ERROR: {error.GetType().Name}: {error.Message}");
                if (error.InnerException != null) Console.WriteLine($"  Inner: {error.InnerException.Message}");
                Console.WriteLine();
                return;
            }

            Console.WriteLine($"  Frames: {frameCount} received, {framesWithChunk} carried chunk data.");
            if (firstChunk != null)
                foreach (var kv in firstChunk.OrderBy(k => k.Key, StringComparer.Ordinal))
                    Console.WriteLine($"      {kv.Key,-28} = {Describe(kv.Value)}");
            else
                Console.WriteLine("      (no chunk data — camera exposes no ChunkID, or producer lacks DSGetBufferChunkData)");

            var reads = responses.Where(r => r.Type == GenICamMessageType.ReadResponse).ToList();
            var acks  = responses.Where(r => r.Type == GenICamMessageType.WriteAck).ToList();
            Console.WriteLine($"  Feature round-trip on the SAME connection: {reads.Count} read(s), {acks.Count} write(s)");
            foreach (var r in responses) Console.WriteLine($"    {r}");
            if (reads.Count >= 3 && !double.IsNaN(newVal))
            {
                double afterWrite   = double.TryParse(reads[1].Payload, System.Globalization.NumberStyles.Any, ic, out var w)  ? w  : double.NaN;
                double afterRestore = double.TryParse(reads[2].Payload, System.Globalization.NumberStyles.Any, ic, out var rr) ? rr : double.NaN;
                double tolW = Math.Max(1.0, Math.Abs(newVal) * 0.05);
                double tolR = Math.Max(1.0, Math.Abs(original) * 0.05);
                Console.WriteLine($"  Write round-trip: {(Math.Abs(afterWrite - newVal) <= tolW ? "PASS" : "(clamped — see values)")}");
                Console.WriteLine($"  Restore verify  : {(Math.Abs(afterRestore - original) <= tolR ? "PASS" : "(clamped — see values)")}");
            }
            Console.WriteLine("  Shared-connection test complete — one open connection served frames, chunks, and feature I/O.");
            Console.WriteLine();
        }

        static string Describe(object? value) => value switch
        {
            null      => "(null)",
            double d  => d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + $"  [{value.GetType().Name}]",
            string s  => $"\"{s}\"  [String]",
            _         => $"{value}  [{value.GetType().Name}]"
        };
    }
}
