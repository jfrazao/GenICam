using System;
using System.Text;

namespace Bonsai.GenICam.GenTL
{
    /// <summary>Marshalling helpers shared across the GenTL P/Invoke wrappers.</summary>
    internal static class GenTLMarshal
    {
        /// <summary>Delegate for the two-call string-fetching pattern used throughout GenTL.</summary>
        internal delegate int StringGetter(byte[] buf, ref UIntPtr size);

        /// <summary>Reads a null-terminated ASCII string by first probing for the size, then filling.</summary>
        internal static string FetchStringRef(StringGetter getter)
        {
            var size = new UIntPtr(256);
            var buf = new byte[256];
            int err = getter(buf, ref size);
            if (err == (int)GCError.GC_ERR_BUFFER_TOO_SMALL)
            {
                buf = new byte[(int)size];
                GenTLException.Check(getter(buf, ref size));
            }
            else
            {
                GenTLException.Check(err);
            }
            int len = Array.IndexOf(buf, (byte)0);
            return Encoding.ASCII.GetString(buf, 0, len < 0 ? buf.Length : len);
        }
    }
}
