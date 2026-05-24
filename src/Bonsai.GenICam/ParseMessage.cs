using System;
using System.ComponentModel;
using System.Globalization;
using System.Reactive.Linq;
using Bonsai;
using OpenCV.Net;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Extracts a <see cref="double"/> value from a <see cref="GenICamMessageType.ReadResponse"/> message.
    /// Non-response messages and messages with unparsable payloads are silently skipped.
    /// </summary>
    [Description("Extracts a double value from each GenICam ReadResponse message in the stream.")]
    public class ParseFloatMessage : Combinator<GenICamMessage, double>
    {
        /// <inheritdoc/>
        public override IObservable<double> Process(IObservable<GenICamMessage> source)
        {
            return source
                .Where(m => m.Type == GenICamMessageType.ReadResponse && m.Payload != null)
                .Select(m => double.Parse(m.Payload!, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Extracts a <see cref="long"/> value from a <see cref="GenICamMessageType.ReadResponse"/> message.
    /// Non-response messages and messages with unparsable payloads are silently skipped.
    /// </summary>
    [Description("Extracts a long integer value from each GenICam ReadResponse message in the stream.")]
    public class ParseIntMessage : Combinator<GenICamMessage, long>
    {
        /// <inheritdoc/>
        public override IObservable<long> Process(IObservable<GenICamMessage> source)
        {
            return source
                .Where(m => m.Type == GenICamMessageType.ReadResponse && m.Payload != null)
                .Select(m => long.Parse(m.Payload!, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Extracts a <see cref="bool"/> value from a <see cref="GenICamMessageType.ReadResponse"/> message.
    /// Accepts "True"/"False" (case-insensitive). Non-response messages are silently skipped.
    /// </summary>
    [Description("Extracts a bool value from each GenICam ReadResponse message in the stream.")]
    public class ParseBoolMessage : Combinator<GenICamMessage, bool>
    {
        /// <inheritdoc/>
        public override IObservable<bool> Process(IObservable<GenICamMessage> source)
        {
            return source
                .Where(m => m.Type == GenICamMessageType.ReadResponse && m.Payload != null)
                .Select(m => bool.Parse(m.Payload!));
        }
    }

    /// <summary>
    /// Extracts the raw payload string from a <see cref="GenICamMessageType.ReadResponse"/> message.
    /// Non-response messages are silently skipped.
    /// </summary>
    [Description("Extracts the raw string payload from each GenICam ReadResponse message in the stream.")]
    public class ParseStringMessage : Combinator<GenICamMessage, string>
    {
        /// <inheritdoc/>
        public override IObservable<string> Process(IObservable<GenICamMessage> source)
        {
            return source
                .Where(m => m.Type == GenICamMessageType.ReadResponse && m.Payload != null)
                .Select(m => m.Payload!);
        }
    }

    /// <summary>
    /// Extracts the <see cref="GenICamFrame"/> from a <see cref="GenICamMessageType.Frame"/> message.
    /// All non-frame messages are silently skipped.
    /// </summary>
    [Description("Extracts the frame from each GenICam Frame message in the stream.")]
    public class ParseFrameMessage : Combinator<GenICamMessage, GenICamFrame>
    {
        /// <inheritdoc/>
        public override IObservable<GenICamFrame> Process(IObservable<GenICamMessage> source)
        {
            return source
                .Where(m => m.Type == GenICamMessageType.Frame && m.Frame != null)
                .Select(m => m.Frame!);
        }
    }
}
