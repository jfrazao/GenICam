using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bonsai.GenICam.GenApi;
using Xunit;

namespace Bonsai.GenICam.Tests
{
    /// <summary>
    /// Offline, hardware-free chunk-decode coverage: builds a <see cref="NodeMap"/> from each saved
    /// tested-camera XML fixture and decodes synthetic chunk bytes through <c>TryReadChunk</c>,
    /// exercising the chunk-ID map and the register → mask/sign/endian → SwissKnife/Converter path.
    /// </summary>
    public class ChunkDecodeTests
    {
        static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "testedCameraXml");

        public static IEnumerable<object[]> Fixtures() =>
            Directory.GetFiles(FixtureDir, "*.xml").Select(f => new object[] { Path.GetFileName(f) });

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void EveryDiscoveredChunkFeatureDecodes(string fixtureName)
        {
            var map = new NodeMap(File.ReadAllText(Path.Combine(FixtureDir, fixtureName)));
            var chunkMap = map.ChunkIdToName;

            // 16 bytes of recognizable, non-zero payload (every byte 0xAB) so single- and multi-byte
            // registers both decode to non-zero. Cameras with no <ChunkID> ports yield an empty map
            // and pass vacuously.
            var bytes = Enumerable.Repeat((byte)0xAB, 16).ToArray();

            foreach (var kv in chunkMap)
            {
                object? value = map.TryReadChunk(kv.Value, bytes);
                Assert.True(value != null, $"{fixtureName}: chunk '{kv.Value}' (0x{kv.Key:X8}) decoded to null");
            }
        }
    }
}
