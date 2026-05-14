using System;
using System.Runtime.Serialization;

namespace Bonsai.GenICam.GenTL
{
    [Serializable]
    public class GenTLException : Exception
    {
        public int ErrorCode { get; }

        public GenTLException(int errorCode, string? message = null)
            : base(message ?? $"GenTL error {(GCError)errorCode} ({errorCode})")
        {
            ErrorCode = errorCode;
        }

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
