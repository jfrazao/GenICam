using OpenCV.Net;

namespace Bonsai.GenICam
{
    /// <summary>
    /// A single frame delivered by a GenICam/GenTL camera, carrying pixel data and
    /// per-buffer metadata extracted from the GenTL buffer info layer.
    /// </summary>
    public sealed class GenICamFrame
    {
        /// <summary>Gets the pixel data for this frame.</summary>
        public IplImage Image { get; }

        /// <summary>
        /// Gets the raw device-clock timestamp in ticks (<c>BUFFER_INFO_TIMESTAMP</c>).
        /// The tick frequency is device- and vendor-specific; use the delta between frames
        /// rather than comparing to wall-clock time. Zero if the producer does not implement
        /// this field.
        /// </summary>
        public ulong Timestamp { get; }

        /// <summary>
        /// Gets the device-clock timestamp in nanoseconds (<c>BUFFER_INFO_TIMESTAMP_NS</c>,
        /// GenTL 1.4+). Zero when the producer does not implement this field — use
        /// <see cref="Timestamp"/> (raw ticks) as a fallback in that case.
        /// </summary>
        public ulong TimestampNs { get; }

        /// <summary>
        /// Gets the monotonically increasing frame counter (<c>BUFFER_INFO_FRAMEID</c>).
        /// Gaps between consecutive values indicate dropped frames. Zero if the producer
        /// does not implement this field.
        /// </summary>
        public ulong FrameId { get; }

        /// <summary>
        /// Gets a value indicating whether the buffer was delivered before all pixel data
        /// arrived (<c>BUFFER_INFO_IS_INCOMPLETE</c>). Incomplete frames still contain
        /// partial pixel data.
        /// </summary>
        public bool IsIncomplete { get; }

        /// <summary>Gets the width of the frame in pixels.</summary>
        public int Width => Image.Width;

        /// <summary>Gets the height of the frame in pixels.</summary>
        public int Height => Image.Height;

        /// <summary>Gets the bit depth of each pixel channel.</summary>
        public IplDepth Depth => Image.Depth;

        /// <summary>Gets the number of channels per pixel.</summary>
        public int Channels => Image.Channels;

        internal GenICamFrame(IplImage image, ulong timestamp, ulong timestampNs, ulong frameId, bool isIncomplete)
        {
            Image        = image;
            Timestamp    = timestamp;
            TimestampNs  = timestampNs;
            FrameId      = frameId;
            IsIncomplete = isIncomplete;
        }
    }
}
