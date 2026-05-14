using System;
using System.Collections.Generic;
using OpenCV.Net;

namespace Bonsai.GenICam.GenTL
{
    internal sealed class GenTLDataStream : IDisposable
    {
        private readonly GenTLApi _api;
        private IntPtr _handle;
        private IntPtr _newBufferEvent;
        private readonly List<IntPtr> _buffers = new List<IntPtr>();
        private int _fallbackWidth;
        private int _fallbackHeight;
        private ulong _fallbackPixelFmt;

        internal GenTLDataStream(GenTLApi api, IntPtr handle)
        {
            _api = api;
            _handle = handle;
        }

        internal void SetFallbacks(int width, int height, ulong pixelFmt)
        {
            _fallbackWidth = width;
            _fallbackHeight = height;
            _fallbackPixelFmt = pixelFmt;
        }

        internal void Start(int numBuffers)
        {
            ulong payloadSize;
            try { payloadSize = GetStreamInfoUInt64(StreamInfoCmd.PayloadSize); }
            catch (GenTLException) { payloadSize = 0; }

            if (payloadSize == 0)
                payloadSize = ComputePayloadSizeFromFallbacks();

            if (payloadSize == 0)
                throw new InvalidOperationException(
                    "Cannot determine buffer payload size: DSGetInfo(PayloadSize) is not implemented or returned 0, " +
                    "and Width/Height/PixelFormat are not available from the NodeMap.");

            GenTLException.Check(_api.GCRegisterEvent(
                _handle, (uint)EventType.NewBuffer, out _newBufferEvent));

            for (int i = 0; i < numBuffers; i++)
            {
                GenTLException.Check(_api.DSAllocAndAnnounceBuffer(
                    _handle, (UIntPtr)payloadSize, IntPtr.Zero, out IntPtr hBuf));
                _buffers.Add(hBuf);
                GenTLException.Check(_api.DSQueueBuffer(_handle, hBuf));
            }

            GenTLException.Check(_api.DSStartAcquisition(
                _handle, (uint)AcqStartFlags.Default, ulong.MaxValue));
        }

        internal void Stop()
        {
            if (_handle == IntPtr.Zero) return;
            _api.DSStopAcquisition(_handle, (uint)AcqStopFlags.Default);
            _api.DSFlushQueue(_handle, (uint)AcqQueueType.AllDiscard);
            if (_newBufferEvent != IntPtr.Zero)
            {
                _api.EventKill(_newBufferEvent);
                _api.GCUnregisterEvent(_handle, (uint)EventType.NewBuffer);
                _newBufferEvent = IntPtr.Zero;
            }
            foreach (var hBuf in _buffers)
                _api.DSRevokeBuffer(_handle, hBuf, out _, out _);
            _buffers.Clear();
        }

        // Called from the dispose path on a different thread to unblock a waiting WaitForFrame.
        internal void InterruptWait()
        {
            var ev = _newBufferEvent;
            if (ev != IntPtr.Zero)
                _api.EventKill(ev);
        }

        // Blocks until a buffer arrives (or timeout ms elapses). Returns null on timeout or abort.
        internal IplImage? WaitForFrame(uint timeoutMs)
        {
            // S_EVENT_NEW_BUFFER = { BUFFER_HANDLE BufferHandle; void* pUserPointer; }
            var eventData = new byte[2 * IntPtr.Size];
            var size = new UIntPtr((uint)eventData.Length);
            int err = _api.EventGetData(_newBufferEvent, eventData, ref size, timeoutMs);

            if (err == (int)GCError.GC_ERR_TIMEOUT || err == (int)GCError.GC_ERR_ABORT) return null;
            if (err == (int)GCError.GC_ERR_INVALID_HANDLE) return null; // event was killed
            GenTLException.Check(err);

            IntPtr hBuffer = IntPtr.Size == 8
                ? (IntPtr)BitConverter.ToInt64(eventData, 0)
                : (IntPtr)BitConverter.ToInt32(eventData, 0);

            try
            {
                return ExtractImage(hBuffer);
            }
            finally
            {
                _api.DSQueueBuffer(_handle, hBuffer);
            }
        }

        private IplImage ExtractImage(IntPtr hBuffer)
        {
            int width  = (int)TryGetBufferInfo(hBuffer, BufferInfoCmd.Width,  (ulong)_fallbackWidth);
            int height = (int)TryGetBufferInfo(hBuffer, BufferInfoCmd.Height, (ulong)_fallbackHeight);
            ulong pixelFormat = TryGetBufferInfo(hBuffer, BufferInfoCmd.PixelFormat, _fallbackPixelFmt);
            IntPtr basePtr = GetBufferInfoPtr(hBuffer, BufferInfoCmd.Base);
            ulong sizeFilled = TryGetBufferInfo(hBuffer, BufferInfoCmd.SizeFilled, ulong.MaxValue);

            if (width <= 0 || height <= 0)
                throw new InvalidOperationException(
                    $"Frame dimensions unavailable (width={width}, height={height}). " +
                    "Check that the camera is configured and the GenTL producer fills BUFFER_INFO_WIDTH/HEIGHT.");

            var (depth, channels) = PixelFormatToOpenCv(pixelFormat);
            var image = new IplImage(new Size(width, height), depth, channels);
            long expectedBytes = (long)image.WidthStep * height;

            if (sizeFilled != ulong.MaxValue && (long)sizeFilled < expectedBytes)
                throw new InvalidOperationException(
                    $"Buffer underrun: received {sizeFilled} bytes, expected {expectedBytes}.");

            unsafe
            {
                Buffer.MemoryCopy(
                    (void*)basePtr,
                    (void*)image.ImageData,
                    expectedBytes,
                    expectedBytes);
            }
            return image;
        }

        private ulong TryGetBufferInfo(IntPtr hBuffer, BufferInfoCmd cmd, ulong fallback)
        {
            try { return GetBufferInfoUInt64(hBuffer, cmd); }
            catch { return fallback; }
        }

        private static (IplDepth depth, int channels) PixelFormatToOpenCv(ulong pfnc)
        {
            ulong fmt = pfnc & 0xFFFFFFFF;
            switch (fmt)
            {
                // 8-bit mono / bayer
                case 0x01080001: // Mono8
                case 0x01080008: // BayerGR8
                case 0x01080009: // BayerRG8
                case 0x0108000A: // BayerGB8
                case 0x0108000B: // BayerBG8
                    return (IplDepth.U8, 1);

                // 10/12/16-bit mono / bayer (stored as 16-bit)
                case 0x01100003: // Mono10
                case 0x01100005: // Mono12
                case 0x01100007: // Mono16
                case 0x01100010: // BayerGR10
                case 0x01100011: // BayerRG10
                case 0x01100012: // BayerGB10
                case 0x01100013: // BayerBG10
                case 0x01100014: // BayerGR12
                case 0x01100015: // BayerRG12
                case 0x01100016: // BayerGB12
                case 0x01100017: // BayerBG12
                case 0x0110002E: // BayerGR16
                case 0x0110002F: // BayerRG16
                case 0x01100030: // BayerGB16
                case 0x01100031: // BayerBG16
                    return (IplDepth.U16, 1);

                // 24-bit RGB / BGR
                case 0x02180014: // RGB8
                case 0x02180015: // BGR8
                    return (IplDepth.U8, 3);

                // 48-bit RGB / BGR
                case 0x02300033: // RGB16
                case 0x02300034: // BGR16
                    return (IplDepth.U16, 3);

                default:
                {
                    // Generic PFNC fallback: infer OpenCV type from the PFNC byte fields.
                    // Byte3 (bits 31-24): 0x01 = single-component (mono/bayer), 0x02 = multi-component
                    // Byte2 (bits 23-16): total bits per pixel (0x08=8, 0x10=16, 0x18=24, ...)
                    uint componentType = (uint)((fmt >> 24) & 0xFF);
                    uint bitsPerPixel  = (uint)((fmt >> 16) & 0xFF);

                    if (componentType == 0x01)
                        return bitsPerPixel <= 0x08 ? (IplDepth.U8, 1) : (IplDepth.U16, 1);

                    if (componentType == 0x02)
                    {
                        if (bitsPerPixel == 0x18) return (IplDepth.U8, 3);
                        if (bitsPerPixel == 0x20) return (IplDepth.U8, 4);
                        if (bitsPerPixel == 0x30) return (IplDepth.U16, 3);
                    }

                    throw new NotSupportedException(
                        $"Pixel format 0x{fmt:X8} (componentType=0x{componentType:X2}, bpp=0x{bitsPerPixel:X2}) is not supported.");
                }
            }
        }

        // PFNC bits 23-16 = total bits per pixel (e.g. 0x08 for 8bpp, 0x18 for 24bpp).
        // Used when the producer does not implement DSGetInfo(PayloadSize).
        private ulong ComputePayloadSizeFromFallbacks()
        {
            if (_fallbackWidth <= 0 || _fallbackHeight <= 0 || _fallbackPixelFmt == 0) return 0;
            uint bitsPerPixel = (uint)((_fallbackPixelFmt >> 16) & 0xFF);
            if (bitsPerPixel == 0) return 0;
            ulong bytesPerPixel = ((ulong)bitsPerPixel + 7) / 8;
            return (ulong)_fallbackWidth * (ulong)_fallbackHeight * bytesPerPixel;
        }

        private ulong GetStreamInfoUInt64(StreamInfoCmd cmd)
        {
            var buf = new byte[8];
            var size = new UIntPtr(8);
            GenTLException.Check(_api.DSGetInfo(_handle, (uint)cmd, out _, buf, ref size));
            return BitConverter.ToUInt64(buf, 0);
        }

        private ulong GetBufferInfoUInt64(IntPtr hBuffer, BufferInfoCmd cmd)
        {
            var buf = new byte[8];
            var size = new UIntPtr(8);
            GenTLException.Check(_api.DSGetBufferInfo(_handle, hBuffer, (uint)cmd, out _, buf, ref size));
            return BitConverter.ToUInt64(buf, 0);
        }

        private IntPtr GetBufferInfoPtr(IntPtr hBuffer, BufferInfoCmd cmd)
        {
            var buf = new byte[IntPtr.Size];
            var size = new UIntPtr((uint)buf.Length);
            GenTLException.Check(_api.DSGetBufferInfo(_handle, hBuffer, (uint)cmd, out _, buf, ref size));
            return IntPtr.Size == 8
                ? (IntPtr)BitConverter.ToInt64(buf, 0)
                : (IntPtr)BitConverter.ToInt32(buf, 0);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                Stop();
                _api.DSClose(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}
