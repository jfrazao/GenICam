using System.IO;
using System.Linq;
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
        static NodeMap LoadFixture(string fileName)
        {
            var path = Path.Combine(System.AppContext.BaseDirectory, "testedCameraXml", fileName);
            return new NodeMap(File.ReadAllText(path));
        }

        const string Ids = "UI322xCP-M.xml";

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
