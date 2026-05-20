using System;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Filters a <see cref="GenICamMessage"/> stream by feature name and/or message type.
    /// Leave either property unset to pass all values for that criterion.
    /// </summary>
    [Description("Filters a GenICam message stream by feature name and/or message type.")]
    public class FilterMessage : Combinator<GenICamMessage, GenICamMessage>
    {
        /// <summary>Gets or sets the feature name to match. Leave empty to pass messages for all features.</summary>
        [Description("Pass only messages for this feature name. Leave empty to pass all features.")]
        public string? FeatureName { get; set; }

        /// <summary>Gets or sets the message type to match. Leave null to pass all message types.</summary>
        [Description("Pass only messages of this type. Leave null to pass all types.")]
        public GenICamMessageType? MessageType { get; set; }

        /// <inheritdoc/>
        public override IObservable<GenICamMessage> Process(IObservable<GenICamMessage> source)
        {
            var name = string.IsNullOrWhiteSpace(FeatureName) ? null : FeatureName;
            var type = MessageType;
            return source.Where(msg =>
                (name == null || string.Equals(msg.FeatureName, name, StringComparison.OrdinalIgnoreCase)) &&
                (type == null || msg.Type == type));
        }
    }
}
