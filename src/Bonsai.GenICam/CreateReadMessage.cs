using System;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Creates a <see cref="GenICamMessage"/> read request for the named feature on each upstream element.
    /// Connect to <see cref="GenICamDevice"/> to execute the read.
    /// </summary>
    [Combinator]
    [Description("Creates a GenICam read-request message for the named feature on each upstream element.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class CreateReadMessage
    {
        /// <summary>Gets or sets the name of the GenICam feature to read (e.g. <c>ExposureTime</c>, <c>Gain</c>).</summary>
        [Description("Name of the GenICam feature to read (e.g. ExposureTime, Gain).")]
        public string? FeatureName { get; set; }

        /// <summary>Emits a read-request message for each upstream element, ignoring the element value.</summary>
        public IObservable<GenICamMessage> Process<T>(IObservable<T> source)
        {
            var name = string.IsNullOrWhiteSpace(FeatureName)
                ? throw new InvalidOperationException("CreateReadMessage: FeatureName must be set.")
                : FeatureName;
            return source.Select(_ => GenICamMessage.Read(name));
        }
    }
}
