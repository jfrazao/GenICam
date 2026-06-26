using System;
using OpenCV.Net;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Pixel-format conversions between PFNC (GenICam Pixel Format Naming Convention) codes, GenICam
    /// pixel-format names, and OpenCV image types. Kept together so the mapping isn't split across the
    /// GenTL data-stream wrapper and the device operator.
    /// </summary>
    internal static class PixelFormat
    {
        /// <summary>Maps a PFNC pixel-format code to the OpenCV depth and channel count for the frame buffer.</summary>
        internal static (IplDepth depth, int channels) ToOpenCv(ulong pfnc)
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

        /// <summary>Maps a GenICam pixel-format name (e.g. <c>"Mono8"</c>) to its PFNC code; 0 if unknown.</summary>
        internal static ulong NameToCode(string name)
        {
            switch (name)
            {
                case "Mono8":    return 0x01080001;
                case "Mono10":   return 0x01100003;
                case "Mono12":   return 0x01100005;
                case "Mono16":   return 0x01100007;
                case "RGB8":     return 0x02180014;
                case "BGR8":     return 0x02180015;
                case "BayerGR8": return 0x01080008;
                case "BayerRG8": return 0x01080009;
                case "BayerGB8": return 0x0108000A;
                case "BayerBG8": return 0x0108000B;
                default:         return 0;
            }
        }
    }
}
