using Bonsai.Expressions;
using Xunit;

namespace Bonsai.GenICam.Tests
{
    /// <summary>
    /// GenICamDevice must be usable as a source (zero input connections) — acquiring frames without a
    /// feature-request stream — as well as a combinator (one input). This builds a single-node workflow
    /// with no inputs and asserts it compiles; before the Process() overload existed this threw
    /// "Unsupported number of arguments. This node requires at least 1 input connection(s)."
    /// Building the workflow does not open any camera (device open is deferred to subscription).
    /// </summary>
    public class SourceUsageTests
    {
        [Fact]
        public void GenICamDevice_BuildsWithZeroInputConnections()
        {
            var workflow = new ExpressionBuilderGraph();
            workflow.Add(new CombinatorBuilder { Combinator = new GenICamDevice() });

            var error = Record.Exception(() => workflow.Build());

            Assert.Null(error);
        }
    }
}
