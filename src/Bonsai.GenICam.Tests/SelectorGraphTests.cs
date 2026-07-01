using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bonsai.GenICam;
using Bonsai.GenICam.GenApi;
using Xunit;

namespace Bonsai.GenICam.Tests
{
    /// <summary>
    /// Offline coverage for the GenICam selector dependency graph (#12, item #9): a selector node's
    /// &lt;pSelected&gt; targets are parsed and exposed via <see cref="NodeMap.GetSelectedFeatures"/>.
    /// The editor uses this to re-read the governed features after a selector write (their values
    /// change as a side effect). Exercised against the IDS UI-3220CP fixture, no camera attached.
    /// </summary>
    public class SelectorGraphTests
    {
        static string FixtureDir => Path.Combine(System.AppContext.BaseDirectory, "testedCameraXml");

        static NodeMap LoadFixture(string fileName) =>
            new NodeMap(File.ReadAllText(Path.Combine(FixtureDir, fileName)));

        public static IEnumerable<object[]> Fixtures() =>
            Directory.GetFiles(FixtureDir, "*.xml").Select(f => new object[] { Path.GetFileName(f) });

        const string Ids = "UI322xCP-M.xml";

        // Generic, future-proof: for EVERY fixture (current and any added later), every inline-<Value>
        // selector must round-trip its value client-side. Register-backed selectors need a port so they
        // don't appear in TryReadAll offline; any enum selector that does is inline-<Value>. Uses the
        // full (unfiltered) entry list since availability guards can't be evaluated without a camera.
        [Theory]
        [MemberData(nameof(Fixtures))]
        public void InlineValueSelectors_RoundTripTheirValue(string fixtureName)
        {
            var map = LoadFixture(fixtureName);
            foreach (var fv in map.TryReadAll().ToList())
            {
                if (!map.IsInlineValueEnum(fv.Name)) continue;             // client-side write path only
                if (map.GetSelectedFeatures(fv.Name).Count == 0) continue; // not a selector
                var entries = map.GetAllEnumEntries(fv.Name);
                if (entries.Count < 2) continue;

                var original = (string)map.Read(fv.Name).Value;
                var target = entries.First(e => !string.Equals(e, original, System.StringComparison.Ordinal));
                map.Write(fv.Name, target);
                Assert.Equal(target, (string)map.Read(fv.Name).Value);
                map.Write(fv.Name, original); // restore in-memory value
            }
        }

        [Fact]
        public void SingleTargetSelector_IsParsed()
        {
            var map = LoadFixture(Ids);
            Assert.Equal(new[] { "DeviceClockFrequency" }, map.GetSelectedFeatures("DeviceClockSelector"));
        }

        [Fact]
        public void MultiTargetSelector_IsParsed()
        {
            var map = LoadFixture(Ids);
            var selected = map.GetSelectedFeatures("BinningSelector");
            Assert.Contains("BinningHorizontal", selected);
            Assert.Contains("BinningVertical", selected);
            Assert.True(selected.Count >= 2);
        }

        [Fact]
        public void NonSelectorNode_HasNoTargets()
        {
            var map = LoadFixture(Ids);
            Assert.Empty(map.GetSelectedFeatures("DeviceClockFrequency"));
        }

        [Fact]
        public void UnknownNode_HasNoTargets()
        {
            var map = LoadFixture(Ids);
            Assert.Empty(map.GetSelectedFeatures("NoSuchFeature_ZZZ"));
        }
    }
}
