using System;
using System.Runtime.InteropServices;

namespace Bonsai.GenICam.GenTL
{
    internal static class NativeMethods
    {
        // LOAD_WITH_ALTERED_SEARCH_PATH: when the path is absolute, Windows adds the DLL's own
        // directory to the dependency search path. Required for GenTL producers that keep their
        // runtime DLLs alongside the .cti file (e.g. HikRobot MVS).
        internal const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false)]
        internal static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
    }
}
