using System.ComponentModel;
using Bonsai.GenICam.GenApi;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Reads a named GenICam feature node from a camera at a specified interval.
    /// </summary>
    [Description("Reads a named GenICam feature node from a camera at a specified interval.")]
    public class GetFeatureNode : GetFeatureNodeBase<FeatureValue>
    {
        /// <inheritdoc/>
        protected override FeatureValue Convert(FeatureValue v) => v;
    }
}
