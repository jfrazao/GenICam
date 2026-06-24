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

        public static void RunLive(string? producerPath, int deviceIndex)
        {
            Console.WriteLine("=== Chunk mode (live capture, ChunkModeActive=true) ===");

            // EnableAllChunks is an internal test-only seam: it makes the device enable every chunk
            // selector the camera exposes (on the acquisition connection, before AcquisitionStart)
            // so the capture exercises the full metadata set. Real workflows leave it off and
            // configure chunks via a UserSet instead.
            var device = new GenICamDevice
            {
                ProducerPath    = producerPath,
                DeviceIndex     = deviceIndex,
                NumBuffers      = 4,
                FrameTimeoutMs  = 5000,
                AcquireFrames   = true,
                ChunkModeActive = true,
                EnableAllChunks = true,
            };

            int frameCount = 0, framesWithChunk = 0;
            var done = new ManualResetEventSlim(false);

            using (device.Process(Observable.Never<GenICamMessage>())
                .Where(m => m.Type == GenICamMessageType.Frame && m.Frame != null)
                .Select(m => m.Frame!)
                .Take(5)
                .Subscribe(
                    frame =>
                    {
                        frameCount++;
                        var chunk = frame.ChunkData;
                        if (chunk != null && chunk.Count > 0)
                        {
                            framesWithChunk++;
                            Console.WriteLine($"  Frame {frameCount}: {chunk.Count} chunk field(s)");
                            foreach (var kv in chunk.OrderBy(k => k.Key, StringComparer.Ordinal))
                                Console.WriteLine($"      {kv.Key,-28} = {Describe(kv.Value)}");
                        }
                        else
                        {
                            Console.WriteLine($"  Frame {frameCount}: no chunk data " +
                                "(producer lacks DSGetBufferChunkData, or camera has no ChunkID in XML)");
                        }
                    },
                    ex =>
                    {
                        Console.WriteLine($"  ERROR: {ex.GetType().Name}: {ex.Message}");
                        if (ex.InnerException != null) Console.WriteLine($"  Inner: {ex.InnerException.Message}");
                        done.Set();
                    },
                    () =>
                    {
                        Console.WriteLine($"  Done — {frameCount} frame(s), {framesWithChunk} carried chunk data.");
                        done.Set();
                    }))
            {
                done.Wait();
            }
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
