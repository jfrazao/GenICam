namespace Bonsai.GenICam
{
    /// <summary>Discriminates the direction and state of a <see cref="GenICamMessage"/>.</summary>
    public enum GenICamMessageType
    {
        /// <summary>Upstream request to read a feature value.</summary>
        ReadRequest,
        /// <summary>Upstream request to write a feature value.</summary>
        WriteRequest,
        /// <summary>Device response carrying the value that was read.</summary>
        ReadResponse,
        /// <summary>Device acknowledgement confirming a write was applied.</summary>
        WriteAck,
        /// <summary>A frame delivered by the acquisition loop.</summary>
        Frame
    }

    /// <summary>
    /// Immutable message flowing through a <see cref="GenICamDevice"/> pipeline.
    /// A null <see cref="Payload"/> marks a read request; a non-null payload carries either
    /// the value to write or the value that was read back.
    /// </summary>
    public sealed class GenICamMessage
    {
        /// <summary>Gets the message type (request, response, ack, or frame).</summary>
        public GenICamMessageType Type { get; }
        /// <summary>Gets the GenICam feature name this message refers to. Empty for Frame messages.</summary>
        public string FeatureName { get; }
        /// <summary>Gets the payload string: null for read requests and frames, a value string for everything else.</summary>
        public string? Payload { get; }
        /// <summary>Gets the captured frame for Frame-type messages; null for all other types.</summary>
        public GenICamFrame? Frame { get; }

        private GenICamMessage(GenICamMessageType type, string featureName, string? payload, GenICamFrame? frame = null)
        {
            Type = type;
            FeatureName = featureName;
            Payload = payload;
            Frame = frame;
        }

        /// <summary>Creates a read-request message for the named feature.</summary>
        public static GenICamMessage Read(string featureName) =>
            new GenICamMessage(GenICamMessageType.ReadRequest, featureName, null);

        /// <summary>Creates a write-request message for the named feature with the given payload.</summary>
        public static GenICamMessage Write(string featureName, string payload) =>
            new GenICamMessage(GenICamMessageType.WriteRequest, featureName, payload);

        internal static GenICamMessage Response(string featureName, string payload) =>
            new GenICamMessage(GenICamMessageType.ReadResponse, featureName, payload);

        internal static GenICamMessage Ack(string featureName, string payload) =>
            new GenICamMessage(GenICamMessageType.WriteAck, featureName, payload);

        internal static GenICamMessage FromFrame(GenICamFrame frame) =>
            new GenICamMessage(GenICamMessageType.Frame, string.Empty, null, frame);

        /// <inheritdoc/>
        public override string ToString() => Type == GenICamMessageType.Frame
            ? $"Frame({Frame?.Width}x{Frame?.Height})"
            : Payload != null ? $"{Type}({FeatureName}={Payload})" : $"{Type}({FeatureName})";
    }
}
