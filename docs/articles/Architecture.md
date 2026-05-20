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
├── GenICamCapture.cs           # Source<GenICamFrame> — streams frames + publishes named connection
├── GenICamFrame.cs             # Frame wrapper: IplImage + timestamp + frameId + isIncomplete
├── GenICamConnectionManager.cs # Named connection registry: Publish/Acquire for connection sharing
├── EnumerateDevices.cs         # Source<DeviceInfo[]> — lists cameras
├── GetFeatureNode.cs           # Source<FeatureValue> — reads a named feature
├── GetFeatureNodeBase.cs       # Abstract Source<T> base + GetIntFeature, GetFloatFeature,
│                               #   GetBoolFeature, GetStringFeature (typed variants)
├── SetFeatureNode.cs           # Combinator — writes a named feature; accepts FeatureValue upstream
│                               #   or fixed Value string
├── SetFeatureNodeBase.cs       # Abstract Combinator<T,T> base + SetIntFeature, SetFloatFeature,
│                               #   SetBoolFeature, SetStringFeature (typed variants)
├── ListFeatureValues.cs        # Source<FeatureValue[]> — reads all readable features
├── FeatureConfiguration.cs     # FeatureOverride list, editor form, UITypeEditors,
│                               #   FeatureCategoryEditor, FeatureNameEditor
├── FeatureRoundTripTester.cs   # Diagnostic: write/readback test for named features
├── GenICamXmlExtractor.cs      # Static helper — fetches raw GenICam XML from a device
├── GenICamDeviceContext.cs     # IDisposable wrapping api+system+iface+device+port
│
├── DeviceInfo.cs               # Struct: index, vendor, model, serial, TL type
├── FeatureValue.cs             # Discriminated union: int/double/string/bool/enum
│
├── GenTL/
│   ├── GenTLLoader.cs          # Scans GENICAM_GENTL64_PATH, loads .cti files
│   ├── GenTLApi.cs             # Delegate types + GetProcAddress binding per producer
│   ├── GenTLTypes.cs           # GC_ERROR, handle typedefs, enums (BUFFER_INFO_CMD etc.)
│   ├── GenTLSystem.cs          # TL_HANDLE wrapper — IDisposable, opens interfaces
│   ├── GenTLInterface.cs       # IF_HANDLE wrapper — enumerates/opens devices
│   ├── GenTLDevice.cs          # DEV_HANDLE wrapper — opens datastreams, exposes port
│   ├── GenTLDataStream.cs      # DS_HANDLE — allocates buffers, starts/stops, fires events
│   ├── GenTLException.cs       # GC_ERROR → GenTLException (message includes error name)
│   └── NativeMethods.cs        # P/Invoke: LoadLibrary, GetProcAddress, FreeLibrary
│
└── GenApi/
    ├── NodeMap.cs              # Fetches XML, builds node tree, read/write by name
    └── NodeTypes.cs            # INode + concrete types: IntegerNode, FloatNode,
                                #   StringNode, BooleanNode, EnumerationNode,
                                #   CommandNode, ConverterNode, IntConverterNode,
                                #   MaskedIntRegNode, IntSwissKnifeNode, SwissKnifeNode
```

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
5. `GetFeatureNode` / `SetFeatureNode` call `NodeMap.GetNode(name)` then cast to the appropriate node type

### Connection sharing — shipped approach (`GenICamConnectionManager`)

**Branch:** `fix/sharing-state-connection` (merged to `main`)

Many GenTL producers do not permit two concurrent `TLOpen` sessions from the same process on the same CTI file. If `GenICamCapture` and `GetFeatureNode` both start and target the same camera, the second open returns zero devices.

The solution is a named connection slot: `GenICamCapture` exposes a `Name` property. Feature operators expose a matching `Connection` property. `GenICamConnectionManager` is a static registry keyed by that name:

1. `GenICamCapture` calls `Publish(name, nodeMap)` after the NodeMap is built — stores the entry and signals waiters.
2. Feature operators call `Acquire(name)` — blocks (via `Monitor.Wait`) up to 10 s until the capture publishes, then returns a ref-counted `SharedNodeMap`.
3. When all refs are released the device is closed.

**Rationale:** Explicit naming decouples selection (which camera) from sharing (which operators share it). The ref-counting ensures the device stays open as long as any operator needs it and closes cleanly when all are done. The blocking `Acquire` is a pragmatic choice — feature operators cannot proceed before the device is open, so blocking is correct; the 10 s timeout surfaces misconfiguration quickly.

**Trade-off:** `Monitor.Wait` parks a thread in an otherwise fully reactive codebase. Two alternatives are being explored on separate branches (see below).

---

### Connection sharing — Alternative A: `BehaviorSubject` (`feature/sharing-state-subject`)

**Branch:** `feature/sharing-state-subject` (not yet created, branches from `main`)

Replaces the blocking `Monitor.Wait` in `Acquire()` with a `BehaviorSubject<NodeMap?>` per name. `GenICamCapture` calls `subject.OnNext(nodeMap)` on startup and `subject.OnNext(null)` on teardown. Feature operators call `Acquire(name)` which returns an `IObservable<NodeMap>` — filters nulls, `.Take(1)`, `.Timeout(10 s)`. The `Observable.Using` + blocking acquire becomes `Acquire().SelectMany(map => ...)`.

**Rationale:** The `BehaviorSubject` expresses "wait until available, then proceed" natively in Rx — no thread parking, no lock, no pulse. `BehaviorSubject` also replays the last value, so a late subscriber gets the current NodeMap immediately without any wait. The external API (`Name`/`Connection` properties) and timeout semantics are unchanged — no workflow migration needed. This is the minimal-risk improvement: same UX, reactive internals.

**Trade-off:** The `NodeMap` is still a shared mutable object — concurrent `GCReadPort`/`GCWritePort` calls from multiple feature operators remain possible (safe per GenTL spec, but not serialized).

---

### Connection sharing — Alternative B: Harp-style message bus (`feature/harp-style`)

**Branch:** `feature/harp-style` (parked at `main` tip, not yet implemented)

A fundamentally different model inspired by how Harp devices work in Bonsai. A single `GenICamDevice` node owns the camera connection. All feature interactions are expressed as messages:

```csharp
public class GenICamMessage
{
    public string FeatureName { get; }
    public string? Payload { get; }  // null = read request, non-null = write value or read response
}
```

```
Timer ──► CreateReadMessage("ExposureTime") ──┐
                                               ├──► GenICamDevice ──► FilterMessage("ExposureTime") ──► ParseFloat
upstream ──► CreateWriteMessage("Gain") ───────┘         │
                                                          └──► (all messages — loggable, replayable)
```

`GenICamDevice : Combinator<GenICamMessage, GenICamMessage>` is the only node that touches `GCReadPort`/`GCWritePort`. It dispatches messages on its own thread, serializing all camera access naturally.

**Rationale:** No `GenICamConnectionManager`, no static state, no blocking. All camera traffic flows through one observable — trivially loggable, replayable, and debuggable. Concurrent access is serialized by the message queue rather than relying on producer thread-safety. `GenICamCapture` could eventually be absorbed as `GenICamDevice` with a `StartAcquisition` message, unifying frame acquisition and feature access on one stream.

**Trade-off:** Every feature read/write requires a `CreateMessage → GenICamDevice → Filter → Parse` chain instead of one node. Acceptable for power users; may be too verbose for casual Bonsai workflows.

#### pIsImplemented / pIsAvailable guards

Some features declare a `<pIsImplemented>` or `<pIsAvailable>` element pointing to another node (typically a `MaskedIntReg`) that evaluates to 0 when the hardware does not support that feature on a given device variant. The GenTL producer enforces this at the `GCWritePort` level — write attempts return `GC_ERR_NOT_IMPLEMENTED` regardless of the node's declared `AccessMode`.

`NodeMap.CanWrite` evaluates these guards before reporting a feature as writable. Features whose guards evaluate to 0 are shown in the feature editor as read-only (greyed out) rather than raising an error when clicked.

**Known case — IDS cameras:** `ExposureAuto`, `GainAuto`, and `BalanceWhiteAuto` have `pIsImplemented` nodes that mask individual bits of an `AutofeatureAvailableReg` register (address `0x16c0`). On cameras where these bits are 0 the features cannot be written via generic GenTL `GCWritePort`. IDS Peak uses a proprietary SDK path to arm these features; there is no equivalent mechanism available through the standard GenTL API.

### Operator signatures

```csharp
// Streams frames while subscribed
public class GenICamCapture : Source<GenICamFrame>
{
    public string?  ProducerPath   { get; set; }   // optional .cti override
    public int      DeviceIndex    { get; set; }   // global index, or index within matching model group
    public string?  CameraModel    { get; set; }   // e.g. "FLIR Blackfly S BFS-U3-16S2M"
    public string?  SerialNumber   { get; set; }   // overrides CameraModel+DeviceIndex when set
    public string?  Name           { get; set; }   // publish connection under this name
    public int      NumBuffers     { get; set; } = 4;
    public uint     FrameTimeoutMs { get; set; } = 5000;
    public FeatureConfiguration Features { get; set; }   // startup feature overrides
}

// Emits once on subscribe
public class EnumerateDevices : Source<DeviceInfo[]>
{
    public string? ProducerPath { get; set; }
}

// Reads a named feature repeatedly at PeriodMs interval (0 = once and complete)
public class GetFeatureNode : Source<FeatureValue>
{
    public string?  ProducerPath    { get; set; }
    public int      DeviceIndex     { get; set; }
    public string?  CameraModel     { get; set; }
    public string?  SerialNumber    { get; set; }
    public string?  Connection      { get; set; }   // share connection from a named GenICamCapture
    public string?  FeatureCategory { get; set; }
    public string?  FeatureName     { get; set; }
    public double   PeriodMs        { get; set; } = 1000;
}

// Writes a named feature on each upstream element, passes element through unchanged.
// When upstream is FeatureValue the value is taken from the element; otherwise Value is used.
public class SetFeatureNode : Combinator
{
    public string?  ProducerPath    { get; set; }
    public int      DeviceIndex     { get; set; }
    public string?  CameraModel     { get; set; }
    public string?  SerialNumber    { get; set; }
    public string?  Connection      { get; set; }   // share connection from a named GenICamCapture
    public string?  FeatureCategory { get; set; }
    public string?  FeatureName     { get; set; }
    public string?  Value           { get; set; }   // fixed value; leave empty when upstream is FeatureValue
}

// Reads all readable features as a single snapshot
public class ListFeatureValues : Source<FeatureValue[]>
{
    public string?  ProducerPath  { get; set; }
    public int      DeviceIndex   { get; set; }
    public string?  CameraModel   { get; set; }
    public string?  SerialNumber  { get; set; }
}

// Typed read operators — emit a concrete .NET type instead of FeatureValue
public abstract class GetFeatureNodeBase<T> : Source<T>
{
    // same camera-selection + Connection + FeatureCategory + FeatureName + PeriodMs as GetFeatureNode
}
public class GetIntFeature    : GetFeatureNodeBase<long>   { }
public class GetFloatFeature  : GetFeatureNodeBase<double> { }
public class GetBoolFeature   : GetFeatureNodeBase<bool>   { }
public class GetStringFeature : GetFeatureNodeBase<string> { }

// Typed write operators — accept a concrete upstream type, format it, write to the feature
public abstract class SetFeatureNodeBase<T> : Combinator<T, T>
{
    // same camera-selection + Connection + FeatureCategory + FeatureName as SetFeatureNode
}
public class SetIntFeature    : SetFeatureNodeBase<long>   { }  // v.ToString()
public class SetFloatFeature  : SetFeatureNodeBase<double> { }  // InvariantCulture
public class SetBoolFeature   : SetFeatureNodeBase<bool>   { }  // "True"/"False"
public class SetStringFeature : SetFeatureNodeBase<string> { }  // pass-through
```
