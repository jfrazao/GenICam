using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Bonsai.GenICam.GenTL
{
    // Holds all GenTL function delegates bound from a single loaded .cti producer.
    // Calling GCInitLib on construction and GCCloseLib + FreeLibrary on dispose.
    internal sealed class GenTLApi : IDisposable
    {
        private IntPtr _module;
        private bool _initialized;

        // ---- delegate types ----

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GCInitLibDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GCCloseLibDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GCGetPortURLDelegate(IntPtr hPort, byte[] sURL, ref UIntPtr piSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GCReadPortDelegate(IntPtr hPort, ulong iAddress, byte[] pBuffer, ref UIntPtr piSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GCWritePortDelegate(IntPtr hPort, ulong iAddress, IntPtr pBuffer, ref UIntPtr piSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GCGetPortInfoDelegate(IntPtr hPort, uint iInfoCmd, out uint piType, byte[] pBuffer, ref UIntPtr piSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GCRegisterEventDelegate(IntPtr hEventSrc, uint iEventID, out IntPtr phEvent);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GCUnregisterEventDelegate(IntPtr hEventSrc, uint iEventID);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int EventGetDataDelegate(IntPtr hEvent, byte[] pBuffer, ref UIntPtr piSize, ulong iTimeout);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int EventFlushDelegate(IntPtr hEvent);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int EventKillDelegate(IntPtr hEvent);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int TLOpenDelegate(out IntPtr phTL);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int TLCloseDelegate(IntPtr hTL);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int TLGetNumInterfacesDelegate(IntPtr hTL, out uint piNumIfaces);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int TLGetInterfaceIDDelegate(IntPtr hTL, uint iIndex, byte[] sID, ref UIntPtr piSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int TLOpenInterfaceDelegate(IntPtr hTL, byte[] sIfaceID, out IntPtr phIface);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int TLUpdateInterfaceListDelegate(IntPtr hTL, out byte pbChanged, ulong iTimeout);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int IFCloseDelegate(IntPtr hIface);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int IFGetNumDevicesDelegate(IntPtr hIface, out uint piNumDevices);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int IFGetDeviceIDDelegate(IntPtr hIface, uint iIndex, byte[] sID, ref UIntPtr piSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int IFUpdateDeviceListDelegate(IntPtr hIface, out byte pbChanged, ulong iTimeout);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int IFOpenDeviceDelegate(IntPtr hIface, byte[] sDeviceID, uint iOpenFlags, out IntPtr phDevice);

        // Optional: added in GenTL 1.3
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int IFGetDeviceInfoDelegate(IntPtr hIface, byte[] sDeviceID, uint iInfoCmd, out uint piType, byte[] pBuffer, ref UIntPtr piSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DevCloseDelegate(IntPtr hDevice);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DevGetPortDelegate(IntPtr hDevice, out IntPtr phRemoteDevice);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DevGetNumDataStreamsDelegate(IntPtr hDevice, out uint piNumDataStreams);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DevGetDataStreamIDDelegate(IntPtr hDevice, uint iIndex, byte[] sID, ref UIntPtr piSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DevOpenDataStreamDelegate(IntPtr hDevice, byte[] sDataStreamID, out IntPtr phDataStream);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DevGetInfoDelegate(IntPtr hDevice, uint iInfoCmd, out uint piType, byte[] pBuffer, ref UIntPtr piSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DSCloseDelegate(IntPtr hDataStream);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DSAllocAndAnnounceBufferDelegate(IntPtr hDataStream, UIntPtr iSize, IntPtr pPrivate, out IntPtr phBuffer);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DSQueueBufferDelegate(IntPtr hDataStream, IntPtr hBuffer);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DSStartAcquisitionDelegate(IntPtr hDataStream, uint iStartFlags, ulong iNumToAcquire);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DSStopAcquisitionDelegate(IntPtr hDataStream, uint iStopFlags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DSFlushQueueDelegate(IntPtr hDataStream, uint iOperation);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DSRevokeBufferDelegate(IntPtr hDataStream, IntPtr hBuffer, out IntPtr ppBuffer, out IntPtr ppPrivate);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DSGetBufferInfoDelegate(IntPtr hDataStream, IntPtr hBuffer, uint iInfoCmd, out uint piType, byte[] pBuffer, ref UIntPtr piSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DSGetInfoDelegate(IntPtr hDataStream, uint iInfoCmd, out uint piType, byte[] pBuffer, ref UIntPtr piSize);

        // ---- bound instances ----

        public GCInitLibDelegate GCInitLib;
        public GCCloseLibDelegate GCCloseLib;
        public GCGetPortURLDelegate GCGetPortURL;
        public GCReadPortDelegate GCReadPort;
        public GCWritePortDelegate GCWritePort;
        public GCGetPortInfoDelegate GCGetPortInfo;
        public GCRegisterEventDelegate GCRegisterEvent;
        public GCUnregisterEventDelegate GCUnregisterEvent;
        public EventGetDataDelegate EventGetData;
        public EventFlushDelegate EventFlush;
        public EventKillDelegate EventKill;
        public TLOpenDelegate TLOpen;
        public TLCloseDelegate TLClose;
        public TLGetNumInterfacesDelegate TLGetNumInterfaces;
        public TLGetInterfaceIDDelegate TLGetInterfaceID;
        public TLOpenInterfaceDelegate TLOpenInterface;
        public TLUpdateInterfaceListDelegate TLUpdateInterfaceList;
        public IFCloseDelegate IFClose;
        public IFGetNumDevicesDelegate IFGetNumDevices;
        public IFGetDeviceIDDelegate IFGetDeviceID;
        public IFUpdateDeviceListDelegate IFUpdateDeviceList;
        public IFOpenDeviceDelegate IFOpenDevice;
        public IFGetDeviceInfoDelegate? IFGetDeviceInfo; // null if producer predates GenTL 1.3
        public DevCloseDelegate DevClose;
        public DevGetPortDelegate DevGetPort;
        public DevGetNumDataStreamsDelegate DevGetNumDataStreams;
        public DevGetDataStreamIDDelegate DevGetDataStreamID;
        public DevOpenDataStreamDelegate DevOpenDataStream;
        public DevGetInfoDelegate DevGetInfo;
        public DSCloseDelegate DSClose;
        public DSAllocAndAnnounceBufferDelegate DSAllocAndAnnounceBuffer;
        public DSQueueBufferDelegate DSQueueBuffer;
        public DSStartAcquisitionDelegate DSStartAcquisition;
        public DSStopAcquisitionDelegate DSStopAcquisition;
        public DSFlushQueueDelegate DSFlushQueue;
        public DSRevokeBufferDelegate DSRevokeBuffer;
        public DSGetBufferInfoDelegate DSGetBufferInfo;
        public DSGetInfoDelegate DSGetInfo;

        // Module-level cache: each .cti is loaded and GCInitLib'd exactly once per process.
        // Many GenTL producers (HikRobot MVS in particular) crash or malfunction if
        // GCCloseLib+FreeLibrary is followed by a second LoadLibrary+GCInitLib in the same process.
        private static readonly Dictionary<string, IntPtr> _moduleCache =
            new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _moduleCacheLock = new object();

        public string ProducerPath { get; }

        public GenTLApi(string ctiPath)
        {
            ctiPath = ctiPath.Trim('"');
            ProducerPath = ctiPath;

            lock (_moduleCacheLock)
            {
                if (!_moduleCache.TryGetValue(ctiPath, out _module))
                {
                    _module = NativeMethods.LoadLibraryExW(ctiPath, IntPtr.Zero, NativeMethods.LOAD_WITH_ALTERED_SEARCH_PATH);
                    if (_module == IntPtr.Zero)
                        throw new InvalidOperationException(
                            $"Failed to load GenTL producer '{ctiPath}': Win32 error {Marshal.GetLastWin32Error()}");
                    _moduleCache[ctiPath] = _module;
                    _initialized = true; // first load: call GCInitLib below
                }
                // else: module already loaded and GCInitLib already called — just rebind delegates
            }

            GCInitLib = Bind<GCInitLibDelegate>("GCInitLib");
            GCCloseLib = Bind<GCCloseLibDelegate>("GCCloseLib");
            GCGetPortURL = Bind<GCGetPortURLDelegate>("GCGetPortURL");
            GCReadPort = Bind<GCReadPortDelegate>("GCReadPort");
            GCWritePort = Bind<GCWritePortDelegate>("GCWritePort");
            GCGetPortInfo = Bind<GCGetPortInfoDelegate>("GCGetPortInfo");
            GCRegisterEvent = Bind<GCRegisterEventDelegate>("GCRegisterEvent");
            GCUnregisterEvent = Bind<GCUnregisterEventDelegate>("GCUnregisterEvent");
            EventGetData = Bind<EventGetDataDelegate>("EventGetData");
            EventFlush = Bind<EventFlushDelegate>("EventFlush");
            EventKill = Bind<EventKillDelegate>("EventKill");
            TLOpen = Bind<TLOpenDelegate>("TLOpen");
            TLClose = Bind<TLCloseDelegate>("TLClose");
            TLGetNumInterfaces = Bind<TLGetNumInterfacesDelegate>("TLGetNumInterfaces");
            TLGetInterfaceID = Bind<TLGetInterfaceIDDelegate>("TLGetInterfaceID");
            TLOpenInterface = Bind<TLOpenInterfaceDelegate>("TLOpenInterface");
            TLUpdateInterfaceList = Bind<TLUpdateInterfaceListDelegate>("TLUpdateInterfaceList");
            IFClose = Bind<IFCloseDelegate>("IFClose");
            IFGetNumDevices = Bind<IFGetNumDevicesDelegate>("IFGetNumDevices");
            IFGetDeviceID = Bind<IFGetDeviceIDDelegate>("IFGetDeviceID");
            IFUpdateDeviceList = Bind<IFUpdateDeviceListDelegate>("IFUpdateDeviceList");
            IFOpenDevice = Bind<IFOpenDeviceDelegate>("IFOpenDevice");
            IFGetDeviceInfo = BindOptional<IFGetDeviceInfoDelegate>("IFGetDeviceInfo");
            DevClose = Bind<DevCloseDelegate>("DevClose");
            DevGetPort = Bind<DevGetPortDelegate>("DevGetPort");
            DevGetNumDataStreams = Bind<DevGetNumDataStreamsDelegate>("DevGetNumDataStreams");
            DevGetDataStreamID = Bind<DevGetDataStreamIDDelegate>("DevGetDataStreamID");
            DevOpenDataStream = Bind<DevOpenDataStreamDelegate>("DevOpenDataStream");
            DevGetInfo = Bind<DevGetInfoDelegate>("DevGetInfo");
            DSClose = Bind<DSCloseDelegate>("DSClose");
            DSAllocAndAnnounceBuffer = Bind<DSAllocAndAnnounceBufferDelegate>("DSAllocAndAnnounceBuffer");
            DSQueueBuffer = Bind<DSQueueBufferDelegate>("DSQueueBuffer");
            DSStartAcquisition = Bind<DSStartAcquisitionDelegate>("DSStartAcquisition");
            DSStopAcquisition = Bind<DSStopAcquisitionDelegate>("DSStopAcquisition");
            DSFlushQueue = Bind<DSFlushQueueDelegate>("DSFlushQueue");
            DSRevokeBuffer = Bind<DSRevokeBufferDelegate>("DSRevokeBuffer");
            DSGetBufferInfo = Bind<DSGetBufferInfoDelegate>("DSGetBufferInfo");
            DSGetInfo = Bind<DSGetInfoDelegate>("DSGetInfo");

            if (_initialized)
                GenTLException.Check(GCInitLib());
            // else: already initialized from a previous load — skip GCInitLib
        }

        // Delegate for the two-call string-fetching pattern used throughout GenTL.
        internal delegate int StringGetter(byte[] buf, ref UIntPtr size);

        // Reads a null-terminated ASCII string by first probing for size then filling.
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

        private T Bind<T>(string name) where T : Delegate
        {
            var ptr = NativeMethods.GetProcAddress(_module, name);
            if (ptr == IntPtr.Zero)
                throw new EntryPointNotFoundException($"GenTL function '{name}' not found in producer '{ProducerPath}'");
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        private T? BindOptional<T>(string name) where T : class, Delegate
        {
            var ptr = NativeMethods.GetProcAddress(_module, name);
            return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        public void Dispose()
        {
            // Intentionally do NOT call GCCloseLib or FreeLibrary here.
            // Many GenTL producers (HikRobot MVS) cannot survive a GCCloseLib+GCInitLib
            // cycle within the same process. The module stays loaded for process lifetime;
            // per-run resource cleanup is handled by TLClose / IFClose / DevClose / DSClose.
        }
    }
}
