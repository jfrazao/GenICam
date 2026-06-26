# Architecture

Two implementation layers:

**GenTL runtime loader** (`src/Bonsai.GenICam/GenTL/`) — Pure C# dynamic P/Invoke. Scans `GENICAM_GENTL64_PATH` for `.cti` producer files, loads them with `LoadLibrary`/`GetProcAddress`, and wraps the GenTL module hierarchy (System → Interface → Device → DataStream → Buffer).

**GenAPI NodeMap** (`src/Bonsai.GenICam/GenApi/`) — Fetches the device XML via `GCReadPort`, parses it, and exposes named feature nodes. Supports Integer, Float, String, Boolean, Enumeration, Command, Converter, IntConverter, MaskedIntReg, IntSwissKnife, and SwissKnife node types. Converter and IntConverter nodes resolve `<pVariable>` references and evaluate `FormulaTo`/`FormulaFrom` expressions with full formula arithmetic.

## Project Structure

```
build/                              # Shared MSBuild configuration
├── Package.props                   # NuGet author, copyright, tags
├── Common.csproj.props             # LangVersion, Nullable, UseArtifactsOutput
├── Common.csproj.targets           # Versioning, package content (icon, license, readme)
└── icon.png                        # Bonsai Foundation package icon

Directory.Build.props               # Auto-imports build/ props for all projects
Directory.Build.targets             # Auto-imports build/ targets for all projects
global.json                         # Pins .NET SDK version

src/Bonsai.GenICam/
├── Bonsai.GenICam.csproj
│
├── GenICamDevice.cs           # Combinator<GenICamMessage, GenICamMessage> — single-owner device;
│                              #   serializes feature reads/writes on EventLoopScheduler;
│                              #   optional concurrent frame acquisition loop
├── GenICamDeviceEditor.cs     # WorkflowComponentEditor — double-click opens the feature editor
├── CameraSelectionEditors.cs  # CameraModelEditor / SerialNumberEditor (camera-selection dropdowns)
├── IGenICamSource.cs          # Camera-selection surface shared by device-opening operators/editors
├── GenICamMessage.cs          # Immutable message: ReadRequest / WriteRequest / ReadResponse /
│                              #   WriteAck / Error / Frame; carries FeatureName + Payload + Frame
├── GenICamFrame.cs            # Frame wrapper: IplImage + Timestamp + TimestampNs +
│                              #   FrameId + IsIncomplete + ChunkData (per-frame metadata)
├── CreateReadMessage.cs       # Transform — emits a ReadRequest message on each upstream element
├── CreateWriteMessage.cs      # Transform — emits a WriteRequest message; typed overloads for
│                              #   string, double, long, bool, FeatureValue
├── FilterMessage.cs           # Combinator — passes messages matching FeatureName and/or MessageType
├── ParseFeature.cs            # Strongly typed output edge (double/long/bool/string) via
│                              #   ExpressionBuilder; non-matching messages silently skipped
├── ParseChunk.cs              # Strongly typed output edge for a named chunk-data field
│                              #   from each frame's GenICamFrame.ChunkData
├── EnumerateDevices.cs        # Source<DeviceInfo[]> — lists cameras
├── ListFeatureValues.cs       # Source<FeatureValue[]> — reads all readable features
├── FeatureConfiguration.cs    # Startup FeatureOverride list + the WinForms feature-editor form
├── FeatureValue.cs            # FeatureValueType enum + FeatureValue: int/double/string/bool/enum
├── DeviceInfo.cs              # Discovered-device info: index, vendor, model, serial, TL type
├── PixelFormat.cs             # PFNC code → OpenCV depth/channels, and PFNC name → code
├── GenICamXmlExtractor.cs     # Static helper — fetches raw GenICam XML from a device
│
├── GenTL/
│   ├── GenTLLoader.cs          # Scans GENICAM_GENTL64_PATH, loads .cti files, locates devices
│   ├── DeviceSession.cs        # Opens a device (serial→model→index) and owns the api/system/
│   │                           #   interface/device stack; exposes Port, lazy NodeMap, DeviceInfo
│   ├── GenTLApi.cs             # Delegate types + GetProcAddress binding per producer
│   ├── GenTLMarshal.cs         # Two-call string-fetch marshalling helper
│   ├── GenTLTypes.cs           # GCError, handle typedefs, enums (BufferInfoCmd etc.)
│   ├── GenTLHandle.cs          # Base for the module wrappers — shared handle close/dispose
│   ├── GenTLSystem.cs          # TL handle — opens interfaces, finds/opens devices
│   ├── GenTLInterface.cs       # IF handle — enumerates/opens devices, reads device info
│   ├── GenTLDevice.cs          # DEV handle — opens datastreams, exposes port
│   ├── GenTLDataStream.cs      # DS handle — allocates buffers, starts/stops, fires events
│   ├── GenTLException.cs       # GCError → GenTLException (message includes error name)
│   └── NativeMethods.cs        # P/Invoke: LoadLibrary, GetProcAddress, FreeLibrary
│
└── GenApi/
    ├── NodeMap.cs              # Fetches XML, builds node tree, read/write by name
    └── NodeTypes.cs            # NodeBase + concrete node types (Integer, Float, String, Boolean,
                                #   Enumeration, Command, Converter/IntConverter, MaskedIntReg,
                                #   SwissKnife/IntSwissKnife) + IRegisterNode / ConverterNodeBase
```

> The diagnostic write/readback tester (`FeatureRoundTripTester`) lives in the `Bonsai.GenICam.LocalGenTLUnitTest` test app, not the library.

## Key Design Decisions

### GenTL dynamic loading

Cannot use `[DllImport]` because the `.cti` filename is unknown at compile time. Pattern:

```csharp
IntPtr hLib = NativeMethods.LoadLibrary(ctiPath);
var pInit = NativeMethods.GetProcAddress(hLib, "GCInitLib");
_GCInitLib = Marshal.GetDelegateForFunctionPointer<GCInitLibDelegate>(pInit);
```

`GenTLApi` holds one set of delegates per loaded producer. `GenTLLoader` selects the producer (first found in `GENICAM_GENTL64_PATH`, or a user-specified path).

### Buffer acquisition loop

`GenTLDataStream` allocates N buffers (`DSAllocAndAnnounceBuffer`), queues them, starts acquisition. A dedicated background thread waits on the new-buffer event (`EventGetData` on `EVENT_NEW_BUFFER`), copies pixel data into an `IplImage`, re-queues the buffer, and calls `observer.OnNext`. Thread is cancelled via `CancellationToken` on dispose.

### IplImage construction

Buffer metadata (width, height, pixel format) from `DSGetBufferInfo`. Pixel format mapped to `IplDepth`/channels:

- `Mono8` → 8-bit 1ch
- `BayerRG8/GB8/GR8/BG8` → 8-bit 1ch (demosaicing optional)
- `RGB8/BGR8` → 8-bit 3ch
- `Mono16` → 16-bit 1ch

### GenAPI NodeMap

1. `GCGetPortURL` → returns `"local:DeviceName.xml;address;length"` or `"file:..."` URL
2. For `local:` scheme: `GCReadPort` at given address/length → raw XML bytes
3. `XDocument.Parse` the XML → build `Dictionary<string, INode>`
4. Node `pAddress` + `Length` + `AccessMode` from XML drives `GCReadPort`/`GCWritePort`
5. `NodeMap.Read(name)` / `NodeMap.Write(name, value)` resolve the node via `GetNode(name)`, then read or coerce-and-write through the appropriate node type

### Camera connection — single owner (`GenICamDevice`)

There is no connection manager or sharing mechanism. A single `GenICamDevice` node owns the camera connection for its subscription lifetime. All camera interactions are expressed as `GenICamMessage` values flowing through that one node:

- Feature requests (`ReadRequest`, `WriteRequest`) arrive on the input stream and are dispatched on an internal `EventLoopScheduler` — all `GCReadPort`/`GCWritePort` calls are serialized on one thread.
- The device emits `ReadResponse`, `WriteAck`, and (when `AcquireFrames = true`) `Frame` messages on a single output stream.
- When `AcquireFrames` is true a second background thread runs the acquisition loop concurrently with the scheduler, pushing `Frame` messages via a synchronized observer.
- Downstream operators use `FilterMessage` and `ParseFeature` to route and extract values from the mixed output stream.

Typical workflow pattern:

```
Timer ──► CreateReadMessage("ExposureTime") ──────────────┐
                                                           ├──► GenICamDevice ──► ParseFeature("ExposureTime", Float) ──► double
Timer ──► Multiply(1000) ──► CreateWriteMessage("ExposureTime") ┘         │
                                                                           └──► FilterMessage(MessageType=Frame) ──► MemberSelector(Frame) ──► GenICamFrame
```

**Why single owner:** No static state, no `Acquire()`/blocking, no ref-counting, no concurrent NodeMap access. All camera traffic flows through one observable — loggable, replayable, and debuggable. Concurrent access to the NodeMap from multiple operators is impossible by construction.

**Trade-off:** Every feature read/write requires a `CreateMessage → GenICamDevice → Filter → Parse` chain in the workflow rather than a single dedicated node. Acceptable for the complete visibility it provides into camera interactions.

#### pIsImplemented / pIsAvailable guards

Some features declare a `<pIsImplemented>` or `<pIsAvailable>` element pointing to another node (typically a `MaskedIntReg`) that evaluates to 0 when the hardware does not support that feature on a given device variant. The GenTL producer enforces this at the `GCWritePort` level — write attempts return `GC_ERR_NOT_IMPLEMENTED` regardless of the node's declared `AccessMode`.

`NodeMap.CanWrite` evaluates these guards before reporting a feature as writable. Features whose guards evaluate to 0 are shown in the feature editor as read-only (greyed out) rather than raising an error when clicked.

**Known case — IDS cameras:** `ExposureAuto`, `GainAuto`, and `BalanceWhiteAuto` have `pIsImplemented` nodes that mask individual bits of an `AutofeatureAvailableReg` register (address `0x16c0`). On cameras where these bits are 0 the features cannot be written via generic GenTL `GCWritePort`. IDS Peak uses a proprietary SDK path to arm these features; there is no equivalent mechanism available through the standard GenTL API.

### Operator signatures

```csharp
// Single-owner device: routes feature messages, optionally runs the acquisition loop
public class GenICamDevice : Combinator<GenICamMessage, GenICamMessage>
{
    public string?  ProducerPath   { get; set; }   // optional .cti override
    public int      DeviceIndex    { get; set; }   // global index, or index within matching model group
    public string?  CameraModel    { get; set; }   // e.g. "FLIR Blackfly S BFS-U3-16S2M"
    public string?  SerialNumber   { get; set; }   // overrides CameraModel+DeviceIndex when set
    public int      NumBuffers     { get; set; } = 4;
    public uint     FrameTimeoutMs { get; set; } = 5000;
    public FeatureConfiguration Features { get; set; }   // startup feature overrides
    public bool     AcquireFrames  { get; set; } = true; // false = feature-only, no streaming
}

// Creates a read-request message on each upstream element (any type triggers a new message)
[Combinator]
public class CreateReadMessage
{
    public string? FeatureName { get; set; }
    public IObservable<GenICamMessage> Process<T>(IObservable<T> source);
}

// Creates a write-request message on each upstream element, formatting the value as payload
[Combinator]
public class CreateWriteMessage
{
    public string? FeatureName { get; set; }
    public IObservable<GenICamMessage> Process(IObservable<string>       source);
    public IObservable<GenICamMessage> Process(IObservable<double>       source);  // InvariantCulture
    public IObservable<GenICamMessage> Process(IObservable<long>         source);
    public IObservable<GenICamMessage> Process(IObservable<bool>         source);  // "True"/"False"
    public IObservable<GenICamMessage> Process(IObservable<FeatureValue> source);
}

// Passes only messages that match both criteria; null means "all"
public class FilterMessage : Combinator<GenICamMessage, GenICamMessage>
{
    public string?             FeatureName { get; set; }
    public GenICamMessageType? MessageType { get; set; }
}

// Extract a named feature value with a typed output edge; type is resolved at workflow compile time
public class ParseFeature : SingleArgumentExpressionBuilder
{
    public string           FeatureName { get; set; }
    public FeatureValueType FeatureType { get; set; }  // Float → double | Integer → long | Boolean → bool | String/Enumeration → string
}

// Unchanged utility operators
public class EnumerateDevices  : Source<DeviceInfo[]>   { public string? ProducerPath { get; set; } }
public class ListFeatureValues : Source<FeatureValue[]> { /* same camera-selection props as GenICamDevice */ }
```
