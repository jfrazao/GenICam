using System;
using System.Runtime.Serialization;

namespace Bonsai.GenICam.GenTL
{
    /// <summary>
    /// The exception thrown when a GenTL API call returns a non-success error code.
    /// </summary>
    [Serializable]
    public class GenTLException : Exception
    {
        /// <summary>Gets the raw <see cref="GCError"/> integer returned by the GenTL call.</summary>
        public int ErrorCode { get; }

        /// <summary>Initializes a new <see cref="GenTLException"/> for the given GenTL error code.</summary>
        public GenTLException(int errorCode, string? message = null)
            : base(message ?? $"GenTL error {(GCError)errorCode} ({errorCode})")
        {
            ErrorCode = errorCode;
        }

        /// <summary>Deserialization constructor required by <see cref="Exception"/>.</summary>
        protected GenTLException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }

        internal static void Check(int error)
        {
            if (error != (int)GCError.GC_ERR_SUCCESS)
                throw new GenTLException(error);
        }
    }
}
