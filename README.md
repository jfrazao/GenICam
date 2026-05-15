# Bonsai.GenICam

A [Bonsai](https://bonsai-rx.org) package for acquiring images and reading/writing features from any GenICam/GenTL-compliant camera — USB3 Vision, GigE Vision, CoaXPress — without a proprietary SDK.

## Prerequisites

- Windows x64
- [.NET Framework 4.7.2](https://dotnet.microsoft.com/download/dotnet-framework/net472)
- The **camera vendor runtime** installed for the camera you intend to use. The runtime ships a GenTL producer (`.cti` file) and registers it by adding its folder to the `GENICAM_GENTL64_PATH` environment variable automatically. Examples:
  - **Basler** — [Pylon Camera Software Suite](https://www.baslerweb.com/pylon) (provides `PylonUsb.cti`, `PylonGigE.cti`)
  - **IDS** — [IDS peak / uEye](https://en.ids-imaging.com/ids-peak.html) (provides `idsGenTL.cti`)
  - **FLIR / Teledyne** — [Spinnaker SDK](https://www.flir.com/products/spinnaker-sdk/) (provides `FLIR_GenTL_v3_4.cti`)
  - **Allied Vision** — [Vimba X](https://www.alliedvision.com/vimba) (provides `VimbaUSBTL.cti`, `VimbaGigETL.cti`)
  - **HIKVISION** — [MVS SDK](https://www.hikrobotics.com/en/machinevision/service/download) (provides `MvGenTLProducer.cti`)

  After installation, verify the variable is set: `echo $env:GENICAM_GENTL64_PATH` should return one or more paths containing `.cti` files.

## Bonsai Operators

| Operator | Type | Description |
|---|---|---|
| `GenICamCapture` | `Source<IplImage>` | Streams frames from a camera |
| `EnumerateDevices` | `Source<DeviceInfo[]>` | Lists all detected GenICam cameras |
| `GetFeatureNode` | `Source<FeatureValue>` | Reads a named feature (e.g. `ExposureTime`) |
| `SetFeatureNode` | `Combinator` | Writes a named feature on each upstream element |
| `ListFeatureValues` | `Source<FeatureValue[]>` | Reads all readable features from a device |

### Using in Bonsai

1. Run the local Bonsai environment (first run downloads Bonsai automatically):
   ```
   .\run-bonsai.ps1
   ```
2. In the Bonsai editor, the `Bonsai.GenICam` package appears in the toolbox under **GenICam**.
3. Set `DeviceIndex` to select which camera (0-based, matches `EnumerateDevices` output).

### Operator properties

**GenICamCapture**
- `ProducerPath` — optional path to a specific `.cti` file (leave blank to use `GENICAM_GENTL64_PATH`)
- `DeviceIndex` — zero-based camera index; when `CameraModel` is set, counts only within the cameras matching that model (default `0`)
- `CameraModel` — optional vendor+model string (e.g. `Basler Blackfly S BFS-U3-16S2M`); click the dropdown to pick from all detected cameras. Enumerated on-demand when the dropdown opens. When set, camera selection at runtime uses model name + `DeviceIndex` rather than global index.
- `SerialNumber` — optional serial number; click the dropdown to pick from all detected cameras. When set, takes priority over `CameraModel` and `DeviceIndex`. A mismatch at startup causes an error — use this to pin a workflow to one specific physical camera.
- `NumBuffers` — acquisition buffer count (default `4`)
- `FrameTimeoutMs` — per-frame timeout in ms (default `5000`)
- `Features` — list of feature overrides applied at startup; click `...` to open the feature editor

#### Camera selection priority

At workflow start, the device is selected in this order:

1. **SerialNumber set** — search all producers (or the configured `ProducerPath`) for that exact serial; error if not found
2. **CameraModel set** — search producers, filter by `"Vendor Model"` string, pick by `DeviceIndex` within the matching set; error if no match or index out of range
3. **Neither** — global `DeviceIndex` across all producers (the default behaviour)

#### Feature overrides

The `Features` property stores a flat list of named feature values. Open the editor by clicking `...` on the property.

**Storage** — serialized as `<Feature>` elements inside the workflow `.bonsai` file alongside `CameraModel`:

```xml
<p1:CameraModel>IDS UI-3220CP-M</p1:CameraModel>
<p1:Features>
  <p1:Feature name="ExposureTime" value="10000" />
  <p1:Feature name="Gain" value="0" />
</p1:Features>
```

Persisted by Bonsai's normal **Save workflow** (Ctrl+S).

**Automatic reset on camera identity change** — the override list is cleared automatically when the camera selection changes in a way that implies a different model:

- **`CameraModel` changes** — always resets (different model, different feature set)
- **`DeviceIndex` or `SerialNumber` changes with no `CameraModel` set** — resets (global index / serial with no model filter, new camera could be a different model)
- **`DeviceIndex` or `SerialNumber` changes while `CameraModel` is set** — no reset (index/serial refine the unit within the same model group; feature set is unchanged)

**In the editor**

- The grid shows all readable features with their current live values. The **Startup** column (checkbox) marks which features are in the override list.
- **Writing a value while connected** — written immediately to the camera (live node map if the workflow is running, design-time connection if not). The value read back from the camera after the write is stored in the override list and the Startup checkbox is ticked automatically. If the camera rejects the write, an error dialog is shown and the override list is not updated.
- **Toggling the Startup checkbox** — adds or removes the feature from the override list without writing to the camera. Useful for including a value that is already set on the camera.
- **Editor opened when the camera is not reachable** — shows the stored override list only (no live values). Any value typed goes straight into the list without a hardware round-trip, so no write errors can occur.

**At workflow start**

1. The device is opened (using `SerialNumber`, `CameraModel`, or `DeviceIndex` — see priority above). If the device cannot be found, an error is thrown and no overrides are applied.
2. Each override is written to the camera in list order. Individual write failures are silently skipped (best-effort); the workflow starts regardless.

**GetFeatureNode / SetFeatureNode**
- `FeatureName` — GenICam XML feature name, e.g. `ExposureTime`, `Gain`, `AcquisitionFrameRate`
- `Value` *(SetFeatureNode only)* — value as a string, parsed to the node's type at runtime

## Running the TestApp

`Bonsai.GenICam.TestApp` is a console tool for verifying your GenTL setup without Bonsai.

### Build

```powershell
dotnet build src/Bonsai.GenICam.TestApp/Bonsai.GenICam.TestApp.csproj -c Release
```

### Run

```powershell
# From the project root:
.\artifacts\bin\Bonsai.GenICam.TestApp\release_win-x64\Bonsai.GenICam.TestApp.exe [device-index]
```

`device-index` selects which camera to use for feature listing and frame capture (defaults to `1`).

### What it does

1. **Enumerates** all GenTL cameras and prints vendor/model/serial — verifies producer loading and device discovery
2. **Extracts GenICam XML** from every detected camera and saves each file to `example-camera-xml/` next to the exe — verifies `GCReadPort` and XML parsing
3. **Lists all readable features** for the target device — verifies the GenAPI NodeMap across all node types
4. **Write/readback round-trip test** for `ExposureTime` and `Gain` — writes a test value, reads it back, then restores the original; verifies Converter formula evaluation and the write path
5. **Captures 5 frames** from the target device and prints dimensions — verifies the buffer acquisition loop and `IplImage` construction

Running it successfully end-to-end confirms that GenTL producer loading, device enumeration, feature access, and image acquisition all work with your camera and driver.

### Example output

```
=== Bonsai.GenICam Test ===

Enumerating GenICam devices...
Found 2 device(s):
  [0] Basler Blackfly S BFS-U3-16S2M s/n=00000000
  [1] IDS UI-3220CP-M s/n=4104084462

=== Extracting GenICam XML from all cameras ===

--- Camera 0: Basler Blackfly S BFS-U3-16S2M (S/N: 00000000) ---
XML length: 1085764 bytes
Saved to: ...\example-camera-xml\camera_0_Blackfly_S_BFS-U3-16S2M.xml

--- Camera 1: IDS UI-3220CP-M (S/N: 4104084462) ---
XML length: 380184 bytes
Saved to: ...\example-camera-xml\camera_1_UI322xCP-M.xml

All readable features of device 1:
  DeviceVendorName = IDS Imaging Development Systems GmbH
  DeviceModelName = UI-3220CP-M
  ExposureTime = 10000
  Gain = 0
  ...

Capturing 5 frames from device 1...
  Frame 1: 1920x1200  depth=U8  ch=1
  Frame 2: 1920x1200  depth=U8  ch=1
  ...
  Done — 5 frame(s) received.
```

### Example XML files

`src/Bonsai.GenICam.TestApp/example-camera-xml/` contains GenICam XML extracted from three real cameras for reference:

| File | Camera |
|---|---|
| `camera_0_Blackfly_S_BFS-U3-16S2M.xml` | Basler Blackfly S (USB3 Vision) |
| `camera_0_UI322xCP-M.xml` | IDS UI-3220CP-M (USB3 Vision) |
| `camera_1_MV-CA013-A0UM.xml` | HIKVISION MV-CA013-A0UM (USB3 Vision) |

## Architecture

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

## License

MIT
