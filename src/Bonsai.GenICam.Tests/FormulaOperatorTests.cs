using System;
using System.IO;
using Bonsai.GenICam.GenApi;
using Xunit;

namespace Bonsai.GenICam.Tests
{
    /// <summary>
    /// Offline coverage for the GenApi-standard SwissKnife equality operator (single <c>=</c>) and
    /// the selector-indexed addressing chain it drives (#12, items 12/13 + formula operators). FLIR's
    /// <c>TriggerSelectorValueToIndex</c> maps the selector value to a register index via
    /// <c>( SWITCH_TARGET = 2 ) ? 0 : ( SWITCH_TARGET = 3 ) ? 1 : 2</c>. Evaluating it exercises:
    /// inline-&lt;Value&gt; enum read (TriggerSelector = 3), numeric enum variable resolution, and the
    /// single-<c>=</c> equality operator — all without a camera.
    /// </summary>
    public class FormulaOperatorTests
    {
        static NodeMap Load(string fileName)
        {
            var path = Path.Combine(System.AppContext.BaseDirectory, "testedCameraXml", fileName);
            return new NodeMap(File.ReadAllText(path));
        }

        [Fact]
        public void GenApiEqualityOperator_ResolvesSelectorIndex()
        {
            // TriggerSelector = 3 (FrameStart) → index 1 via the single-'=' equality formula.
            var map = Load("Blackfly_S_BFS-U3-16S2M.xml");
            Assert.Equal(1L, Convert.ToInt64(map.Read("TriggerSelectorValueToIndex").Value));
        }
    }
}
