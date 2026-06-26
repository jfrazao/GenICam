using System;
using System.ComponentModel;
using System.Globalization;
using System.Reactive.Linq;
using Bonsai;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Creates a <see cref="GenICamMessage"/> write request for the named feature on each upstream element,
    /// formatting the element value as the payload string.
    /// Connect to <see cref="GenICamDevice"/> to execute the write.
    /// </summary>
    [Combinator]
    [Description("Creates a GenICam write-request message for the named feature on each upstream element.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class CreateWriteMessage
    {
        /// <summary>Gets or sets the name of the GenICam feature to write (e.g. <c>ExposureTime</c>, <c>Gain</c>).</summary>
        [Description("Name of the GenICam feature to write (e.g. ExposureTime, Gain).")]
        public string? FeatureName { get; set; }

        private string RequiredName() => string.IsNullOrWhiteSpace(FeatureName)
            ? throw new InvalidOperationException("CreateWriteMessage: FeatureName must be set.")
            : FeatureName;

        /// <summary>Creates a write message whose payload is the upstream <see cref="string"/> value.</summary>
        public IObservable<GenICamMessage> Process(IObservable<string> source)
        { var n = RequiredName(); return source.Select(v => GenICamMessage.Write(n, v)); }

        /// <summary>Creates a write message whose payload is the upstream <see cref="double"/> formatted with invariant culture.</summary>
        public IObservable<GenICamMessage> Process(IObservable<double> source)
        { var n = RequiredName(); return source.Select(v => GenICamMessage.Write(n, v.ToString(CultureInfo.InvariantCulture))); }

        /// <summary>Creates a write message whose payload is the upstream <see cref="long"/> value.</summary>
        public IObservable<GenICamMessage> Process(IObservable<long> source)
        { var n = RequiredName(); return source.Select(v => GenICamMessage.Write(n, v.ToString())); }

        /// <summary>Creates a write message whose payload is the upstream <see cref="bool"/> formatted as "True" or "False".</summary>
        public IObservable<GenICamMessage> Process(IObservable<bool> source)
        { var n = RequiredName(); return source.Select(v => GenICamMessage.Write(n, v ? "True" : "False")); }

        /// <summary>Creates a write message whose payload is taken from an upstream <see cref="FeatureValue"/>.</summary>
        public IObservable<GenICamMessage> Process(IObservable<FeatureValue> source)
        { var n = RequiredName(); return source.Select(v => GenICamMessage.Write(n, v.ToPayloadString())); }
    }
}
