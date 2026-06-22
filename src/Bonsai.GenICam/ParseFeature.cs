using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.Expressions;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Extracts and parses the value of the named GenICam feature from each
    /// <see cref="GenICamMessageType.ReadResponse"/> message in the stream,
    /// emitting a strongly typed output edge (<c>IObservable&lt;double&gt;</c>,
    /// <c>IObservable&lt;long&gt;</c>, etc.) determined at workflow compile time.
    /// Non-matching messages are silently skipped.
    /// </summary>
    [Description("Extracts and parses the named GenICam feature value with a strongly typed output edge.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ParseFeature : SingleArgumentExpressionBuilder
    {
        /// <summary>Gets or sets the GenICam feature name to extract (e.g. <c>ExposureTime</c>, <c>Gain</c>).</summary>
        [Description("GenICam feature name to extract (e.g. ExposureTime, Gain).")]
        public string FeatureName { get; set; } = string.Empty;

        /// <summary>Gets or sets the expected value type of the feature payload.</summary>
        [Description("Expected value type of the feature payload.")]
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

            return Expression.Call(typeof(ParseFeature), helperName, null, source, name);
        }

        static IObservable<double> SelectFloat(IObservable<GenICamMessage> source, string name) =>
            source
                .Where(m => m.Type == GenICamMessageType.ReadResponse
                         && string.Equals(m.FeatureName, name, StringComparison.OrdinalIgnoreCase)
                         && m.Payload != null)
                .Select(m => double.Parse(m.Payload!, CultureInfo.InvariantCulture));

        static IObservable<long> SelectInt(IObservable<GenICamMessage> source, string name) =>
            source
                .Where(m => m.Type == GenICamMessageType.ReadResponse
                         && string.Equals(m.FeatureName, name, StringComparison.OrdinalIgnoreCase)
                         && m.Payload != null)
                .Select(m => long.Parse(m.Payload!, CultureInfo.InvariantCulture));

        static IObservable<bool> SelectBool(IObservable<GenICamMessage> source, string name) =>
            source
                .Where(m => m.Type == GenICamMessageType.ReadResponse
                         && string.Equals(m.FeatureName, name, StringComparison.OrdinalIgnoreCase)
                         && m.Payload != null)
                .Select(m => bool.Parse(m.Payload!));

        static IObservable<string> SelectString(IObservable<GenICamMessage> source, string name) =>
            source
                .Where(m => m.Type == GenICamMessageType.ReadResponse
                         && string.Equals(m.FeatureName, name, StringComparison.OrdinalIgnoreCase)
                         && m.Payload != null)
                .Select(m => m.Payload!);
    }
}
