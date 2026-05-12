namespace Bonsai.GenICam.GenTL
{
    internal enum GCError : int
    {
        GC_ERR_SUCCESS = 0,
        GC_ERR_ERROR = -1001,
        GC_ERR_NOT_INITIALIZED = -1002,
        GC_ERR_NOT_IMPLEMENTED = -1003,
        GC_ERR_RESOURCE_IN_USE = -1004,
        GC_ERR_ACCESS_DENIED = -1005,
        GC_ERR_INVALID_HANDLE = -1006,
        GC_ERR_INVALID_ID = -1007,
        GC_ERR_NO_DATA = -1008,
        GC_ERR_INVALID_PARAMETER = -1009,
        GC_ERR_IO = -1010,
        GC_ERR_TIMEOUT = -1011,
        GC_ERR_ABORT = -1012,
        GC_ERR_INVALID_BUFFER = -1013,
        GC_ERR_NOT_AVAILABLE = -1014,
        GC_ERR_INVALID_ADDRESS = -1015,
        GC_ERR_BUFFER_TOO_SMALL = -1016,
        GC_ERR_INVALID_INDEX = -1017,
        GC_ERR_PARSING_CHUNK_DATA = -1018,
        GC_ERR_INVALID_VALUE = -1019,
        GC_ERR_RESOURCE_EXHAUSTED = -1020,
        GC_ERR_OUT_OF_MEMORY = -1021,
        GC_ERR_BUSY = -1022
    }

    internal enum InfoDataType : uint
    {
        Unknown = 0,
        String = 1,
        StringList = 2,
        Int16 = 3,
        UInt16 = 4,
        Int32 = 5,
        UInt32 = 6,
        Int64 = 7,
        UInt64 = 8,
        Float64 = 9,
        Ptr = 10,
        Bool8 = 11,
        SizeT = 12,
        Buffer = 13,
        PtrDiff = 14
    }

    internal enum DeviceInfoCmd : uint
    {
        ID = 0,
        Vendor = 1,
        Model = 2,
        TLType = 3,
        DisplayName = 4,
        AccessStatus = 5,
        UserDefinedName = 6,
        SerialNumber = 7,
        Version = 8,
        TimestampFrequency = 9
    }

    internal enum DeviceAccessFlags : uint
    {
        Unknown = 0,
        None = 1,
        ReadOnly = 2,
        Control = 3,
        Exclusive = 4
    }

    internal enum AcqStartFlags : uint
    {
        Default = 0
    }

    internal enum AcqStopFlags : uint
    {
        Default = 0,
        Kill = 1
    }

    internal enum AcqQueueType : uint
    {
        InputToOutput = 0,
        OutputDiscard = 1,
        AllToInput = 2,
        UnqueuedToInput = 3,
        AllDiscard = 4
    }

    internal enum EventType : uint
    {
        Error = 0,
        NewBuffer = 1,
        FeatureInvalidate = 2,
        FeatureChange = 3,
        RemoteDevice = 4,
        Module = 5
    }

    internal enum BufferInfoCmd : uint
    {
        Base = 0,
        Size = 1,
        UserPtr = 2,
        Timestamp = 3,
        NewData = 4,
        IsQueued = 5,
        IsAcquiring = 6,
        IsIncomplete = 7,
        TLType = 8,
        SizeFilled = 9,
        Width = 10,
        Height = 11,
        XOffset = 12,
        YOffset = 13,
        XPadding = 14,
        YPadding = 15,
        FrameID = 16,
        ImagePresent = 17,
        ImageOffset = 18,
        PayloadType = 19,
        PixelFormat = 20,
        PixelFormatNamespace = 21,
        DeliveredImageHeight = 22,
        DeliveredChunkPayloadSize = 23,
        ChunkLayoutID = 24,
        Filename = 25,
        PixelEndianness = 26,
        DataSize = 27,
        TimestampNS = 28
    }

    internal enum StreamInfoCmd : uint
    {
        ID = 0,
        NumDelivered = 1,
        NumUnderrun = 2,
        NumAnnounced = 3,
        NumQueued = 4,
        NumAwaitDelivery = 5,
        NumStarted = 6,
        PayloadSize = 7,
        IsGrabbing = 8,
        DefinesPayloadSize = 9,
        TLType = 10,
        NumChunksMax = 11,
        BufAnnounceMin = 12,
        BufAlignment = 13
    }

    internal enum PortInfoCmd : uint
    {
        ID = 0,
        Vendor = 1,
        Model = 2,
        TLType = 3,
        Name = 4,
        PathName = 5,
        Version = 6,
        PortName = 7,
        Description = 8
    }
}
