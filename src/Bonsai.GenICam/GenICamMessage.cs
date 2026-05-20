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
        WriteAck
    }

    /// <summary>
    /// Immutable message flowing through a <see cref="GenICamDevice"/> pipeline.
    /// A null <see cref="Payload"/> marks a read request; a non-null payload carries either
    /// the value to write or the value that was read back.
    /// </summary>
    public sealed class GenICamMessage
    {
        /// <summary>Gets the message type (request, response, or ack).</summary>
        public GenICamMessageType Type { get; }
        /// <summary>Gets the GenICam feature name this message refers to.</summary>
        public string FeatureName { get; }
        /// <summary>Gets the payload string: null for read requests, a value string for everything else.</summary>
        public string? Payload { get; }

        private GenICamMessage(GenICamMessageType type, string featureName, string? payload)
        {
            Type = type;
            FeatureName = featureName;
            Payload = payload;
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

        /// <inheritdoc/>
        public override string ToString() =>
            Payload != null ? $"{Type}({FeatureName}={Payload})" : $"{Type}({FeatureName})";
    }
}
