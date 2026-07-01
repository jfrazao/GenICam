using System.IO;
using System.Linq;
using Bonsai.GenICam.GenApi;
using Xunit;

namespace Bonsai.GenICam.Tests
{
    /// <summary>
    /// Offline coverage for enumeration nodes that hold their value in a node-level &lt;Value&gt;
    /// element instead of a &lt;pValue&gt; register reference (#12). FLIR cameras use this for common
    /// selectors (TriggerSelector, BinningSelector, GainSelector, …). Before the fix these threw on
    /// read and were silently dropped from the editor; now they read their symbolic value and appear
    /// as read-only (there is no backing register to write). Exercised against the BFS fixture.
    /// </summary>
    public class InlineValueEnumTests
    {
        static NodeMap Load(string fileName)
        {
            var path = Path.Combine(System.AppContext.BaseDirectory, "testedCameraXml", fileName);
            return new NodeMap(File.ReadAllText(path));
        }

        const string Bfs = "Blackfly_S_BFS-U3-16S2M.xml";

        [Fact]
        public void InlineValueEnum_ReadsSymbolicValue()
        {
            // TriggerSelector has node-level <Value>3</Value> and entry FrameStart=3, no <pValue>.
            var map = Load(Bfs);
            Assert.Equal("FrameStart", (string)map.Read("TriggerSelector").Value);
        }

        [Fact]
        public void InlineValueEnum_AppearsInReadAll()
        {
            // Was previously skipped by TryReadAll because the read threw (null pValue).
            var map = Load(Bfs);
            Assert.Contains(map.TryReadAll(), fv => fv.Name == "TriggerSelector");
        }

        [Fact]
        public void InlineValueEnum_IsReadOnly()
        {
            // No backing register, so it must not be offered as writable.
            var map = Load(Bfs);
            Assert.False(map.CanWrite("TriggerSelector"));
        }
    }
}
