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
├── GenICamCapture.cs           # Source<IplImage> — streams frames
├── EnumerateDevices.cs         # Source<DeviceInfo[]> — lists cameras
├── GetFeatureNode.cs           # Source<FeatureValue> — reads a named feature
├── SetFeatureNode.cs           # Combinator — writes a named feature + passthrough
├── ListFeatureValues.cs        # Source<FeatureValue[]> — reads all readable features
├── FeatureConfiguration.cs     # FeatureOverride list, editor form, UITypeEditors
├── FeatureRoundTripTester.cs   # Diagnostic: write/readback test for named features
├── GenICamXmlExtractor.cs      # Static helper — fetches raw GenICam XML from a device
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

#### pIsImplemented / pIsAvailable guards

Some features declare a `<pIsImplemented>` or `<pIsAvailable>` element pointing to another node (typically a `MaskedIntReg`) that evaluates to 0 when the hardware does not support that feature on a given device variant. The GenTL producer enforces this at the `GCWritePort` level — write attempts return `GC_ERR_NOT_IMPLEMENTED` regardless of the node's declared `AccessMode`.

`NodeMap.CanWrite` evaluates these guards before reporting a feature as writable. Features whose guards evaluate to 0 are shown in the feature editor as read-only (greyed out) rather than raising an error when clicked.

**Known case — IDS cameras:** `ExposureAuto`, `GainAuto`, and `BalanceWhiteAuto` have `pIsImplemented` nodes that mask individual bits of an `AutofeatureAvailableReg` register (address `0x16c0`). On cameras where these bits are 0 the features cannot be written via generic GenTL `GCWritePort`. IDS Peak uses a proprietary SDK path to arm these features; there is no equivalent mechanism available through the standard GenTL API.

### Operator signatures

```csharp
// Streams frames while subscribed; shares one camera connection per subscriber
public class GenICamCapture : Source<IplImage>
{
    public string  ProducerPath  { get; set; }   // optional .cti override
    public int     DeviceIndex   { get; set; }   // global index, or index within matching model group
    public string? CameraModel   { get; set; }   // e.g. "Basler Blackfly S BFS-U3-16S2M"
    public string? SerialNumber  { get; set; }   // overrides CameraModel+DeviceIndex when set
    public int     NumBuffers    { get; set; } = 4;
    public uint    FrameTimeoutMs { get; set; } = 5000;
    public FeatureConfiguration Features { get; set; }   // startup feature overrides
}

// Emits once on subscribe
public class EnumerateDevices : Source<DeviceInfo[]>
{
    public string ProducerPath { get; set; }
}

// Reads a single named feature on subscribe
public class GetFeatureNode : Source<FeatureValue>
{
    public string ProducerPath { get; set; }
    public int DeviceIndex { get; set; }
    public string FeatureName { get; set; }
}

// Writes a named feature on each upstream element, passes element through unchanged
public class SetFeatureNode : Combinator
{
    public string ProducerPath { get; set; }
    public int DeviceIndex { get; set; }
    public string FeatureName { get; set; }
    public string Value { get; set; }   // parsed to node type at runtime
}

// Reads all readable features as a single snapshot
public class ListFeatureValues : Source<FeatureValue[]>
{
    public string ProducerPath { get; set; }
    public int DeviceIndex { get; set; }
}
```
