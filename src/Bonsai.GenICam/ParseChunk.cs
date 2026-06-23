using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.Expressions;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Extracts and parses the value of the named GenICam chunk data field from each
    /// <see cref="GenICamFrame"/> in the stream, emitting a strongly typed output edge
    /// (<c>IObservable&lt;double&gt;</c>, <c>IObservable&lt;long&gt;</c>, etc.) determined
    /// at workflow compile time. Frames where <see cref="GenICamFrame.ChunkData"/> is null
    /// or does not contain the named field are silently skipped.
    /// Requires <see cref="GenICamDevice.ChunkModeActive"/> to be enabled.
    /// </summary>
    [Description("Extracts and parses the named GenICam chunk data field from each frame with a strongly typed output edge.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ParseChunk : SingleArgumentExpressionBuilder
    {
        /// <summary>Gets or sets the chunk data feature name to extract (e.g. <c>ChunkExposureTime</c>, <c>ChunkFrameID</c>).</summary>
        [Description("Chunk data feature name to extract (e.g. ChunkExposureTime, ChunkFrameID).")]
        public string FeatureName { get; set; } = string.Empty;

        /// <summary>Gets or sets the expected value type of the chunk data field.</summary>
        [Description("Expected value type of the chunk data field.")]
        public FeatureValueType FeatureType { get; set; } = FeatureValueType.Float;

        /// <inheritdoc/>
        public override Expression Build(IEnumerable<Expression> arguments)
        {
            var source = arguments.First();
            var name   = Expression.Constant(FeatureName);

            var helperName = FeatureType switch
            {
                FeatureValueType.Float       => nameof(SelectFloat),
                FeatureValueType.Integer     => nameof(SelectInt),
                FeatureValueType.Boolean     => nameof(SelectBool),
                FeatureValueType.String      => nameof(SelectString),
                FeatureValueType.Enumeration => nameof(SelectString),
                _ => throw new InvalidOperationException($"Unsupported FeatureType: {FeatureType}")
            };

            return Expression.Call(typeof(ParseChunk), helperName, null, source, name);
        }

        static IObservable<double> SelectFloat(IObservable<GenICamFrame> source, string name) =>
            source
                .Where(f => f.ChunkData != null && f.ChunkData.ContainsKey(name))
                .Select(f => Convert.ToDouble(f.ChunkData![name]));

        static IObservable<long> SelectInt(IObservable<GenICamFrame> source, string name) =>
            source
                .Where(f => f.ChunkData != null && f.ChunkData.ContainsKey(name))
                .Select(f => Convert.ToInt64(f.ChunkData![name]));

        static IObservable<bool> SelectBool(IObservable<GenICamFrame> source, string name) =>
            source
                .Where(f => f.ChunkData != null && f.ChunkData.ContainsKey(name))
                .Select(f => Convert.ToBoolean(f.ChunkData![name]));

        static IObservable<string> SelectString(IObservable<GenICamFrame> source, string name) =>
            source
                .Where(f => f.ChunkData != null && f.ChunkData.ContainsKey(name))
                .Select(f => f.ChunkData![name]?.ToString() ?? string.Empty);
    }
}
