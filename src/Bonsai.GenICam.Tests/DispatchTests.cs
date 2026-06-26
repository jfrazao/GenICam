using System.IO;
using System.Linq;
using Bonsai.GenICam.GenApi;
using Xunit;

namespace Bonsai.GenICam.Tests
{
    /// <summary>
    /// Offline coverage for GenICamDevice message dispatch (#13): a rejected read/write or startup
    /// override surfaces as a <see cref="GenICamMessageType.Error"/> message rather than throwing or
    /// faulting the stream. Exercised against a fixture NodeMap with no camera attached.
    /// </summary>
    public class DispatchTests
    {
        const string Bogus = "NoSuchFeature_ZZZ";

        static NodeMap LoadAnyFixture()
        {
            var dir = Path.Combine(System.AppContext.BaseDirectory, "testedCameraXml");
            var file = Directory.GetFiles(dir, "*.xml").OrderBy(f => f).First();
            return new NodeMap(File.ReadAllText(file));
        }

        [Fact]
        public void RejectedWrite_YieldsErrorMessage()
        {
            var msg = GenICamDevice.Dispatch(GenICamMessage.Write(Bogus, "1"), LoadAnyFixture());
            Assert.Equal(GenICamMessageType.Error, msg.Type);
            Assert.Equal(Bogus, msg.FeatureName);
        }

        [Fact]
        public void RejectedRead_YieldsErrorMessage()
        {
            var msg = GenICamDevice.Dispatch(GenICamMessage.Read(Bogus), LoadAnyFixture());
            Assert.Equal(GenICamMessageType.Error, msg.Type);
        }

        [Fact]
        public void RejectedStartupOverride_YieldsErrorMessage()
        {
            var cfg = new FeatureConfiguration();
            cfg.Overrides.Add(new FeatureOverride { Name = Bogus, Value = "1" });
            var results = cfg.Apply(LoadAnyFixture()).ToList();
            Assert.Single(results);
            Assert.Equal(GenICamMessageType.Error, results[0].Type);
        }
    }
}
