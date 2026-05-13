# Plan: Bonsai.GenICam

## Context

NeuroGears needs a GenICam module for Bonsai-rx that works with any GenTL-compliant camera — including USB3 Vision — without requiring a proprietary SDK. The module must be open-source under MIT. It will provide four Bonsai operators: CameraCapture, EnumerateDevices, GetFeatureNode, SetFeatureNode.

---

## Architecture

Two implementation layers:

1. **GenTL runtime loader** — Pure C# dynamic P/Invoke. Scans `GENICAM_GENTL64_PATH` (or user-specified path) for `.cti` producer files, loads them with `LoadLibrary`/`GetProcAddress`, and exposes the GenTL module hierarchy as managed wrappers (System → Interface → Device → DataStream → Buffer).

2. **Lightweight GenAPI NodeMap** — Fetches the device's GenICam XML via `GCGetPortURL`/`GCReadPort`, parses it, and exposes named feature nodes. Handles the six node types that cover ~95% of real-world use: Integer, Float, String, Boolean, Enumeration, Command. Node values translate to/from register reads via `GCWritePort`.

---

## Project Structure

```
Bonsai.GenICam/
├── Bonsai.GenICam.sln
└── Bonsai.GenICam/
    ├── Bonsai.GenICam.csproj
    │
    ├── GenICamCapture.cs           # Source<IplImage>
    ├── EnumerateDevices.cs         # Source<DeviceInfo[]>
    ├── GetFeatureNode.cs           # Source<FeatureValue> (polls named node)
    ├── SetFeatureNode.cs           # Combinator<TSource, TSource> (writes + passthrough)
    │
    ├── DeviceInfo.cs               # Struct: id, vendor, model, serial, TL type
    ├── FeatureValue.cs             # Discriminated union: int/double/string/bool/enum
    │
    ├── GenTL/
    │   ├── GenTLLoader.cs          # Scans GENICAM_GENTL64_PATH, LoadLibrary .cti files
    │   ├── GenTLApi.cs             # Delegate types + GetProcAddress binding per loaded .cti
    │   ├── GenTLTypes.cs           # GC_ERROR, handle typedefs, enums (BUFFER_INFO_CMD etc.)
    │   ├── GenTLSystem.cs          # TL_HANDLE wrapper — IDisposable, opens interfaces
    │   ├── GenTLInterface.cs       # IF_HANDLE wrapper — enumerates/opens devices
    │   ├── GenTLDevice.cs          # DEV_HANDLE wrapper — opens datastreams, exposes port
    │   ├── GenTLDataStream.cs      # DS_HANDLE — allocates buffers, starts/stops, fires events
    │   └── GenTLException.cs       # GC_ERROR → GenTLException (message includes error name)
    │
    ├── GenApi/
    │   ├── NodeMap.cs              # Fetches XML, builds node tree, read/write by name
    │   └── NodeTypes.cs            # INode, IntegerNode, FloatNode, StringNode,
    │                               #   BooleanNode, EnumerationNode, CommandNode
    │
    └── Properties/AssemblyInfo.cs
```

---

## Key Design Decisions

### GenTL dynamic loading
Cannot use `[DllImport]` because the .cti filename is unknown at compile time. Pattern:
```csharp
IntPtr hLib = NativeMethods.LoadLibrary(ctiPath);
var pInit = NativeMethods.GetProcAddress(hLib, "GCInitLib");
_GCInitLib = Marshal.GetDelegateForFunctionPointer<GCInitLibDelegate>(pInit);
```
`GenTLApi` holds one set of delegates per loaded producer. `GenTLLoader` selects the producer (first found, or user-specified path).

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

### Bonsai operator signatures

```csharp
// Source — emits frames while subscribed; shares one camera connection
public class GenICamCapture : Source<IplImage>
{
    public string ProducerPath { get; set; }   // optional .cti override
    public int DeviceIndex { get; set; }
    public int NumBuffers { get; set; } = 4;
}

// Source — emits once on subscribe
public class EnumerateDevices : Source<DeviceInfo[]>
{
    public string ProducerPath { get; set; }
}

// Source — polls at interval, or use as a transform on a trigger sequence
public class GetFeatureNode : Source<FeatureValue>
{
    public string ProducerPath { get; set; }
    public int DeviceIndex { get; set; }
    public string FeatureName { get; set; }
}

// Combinator — writes feature on each element, passes element through unchanged
public class SetFeatureNode : Combinator
{
    public string ProducerPath { get; set; }
    public int DeviceIndex { get; set; }
    public string FeatureName { get; set; }
    public string Value { get; set; }  // parsed to node type at runtime
}
```

---

## .csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <AssemblyName>Bonsai.GenICam</AssemblyName>
    <RootNamespace>Bonsai.GenICam</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Bonsai.Core" Version="2.9.0" />
    <PackageReference Include="OpenCV.Net" Version="3.4.1" />
  </ItemGroup>
</Project>
```

No additional runtime NuGet dependencies. GenTL producers are user-installed (e.g. Basler Pylon, Allied Vision Vimba, FLIR Spinnaker — all provide free GenTL `.cti` producers).

---

## Implementation Phases

### Phase 1 — GenTL plumbing + EnumerateDevices
- `GenTLLoader`, `GenTLApi` (delegates), `GenTLTypes` (enums + error codes)
- `GenTLSystem`, `GenTLInterface`, `GenTLDevice` wrappers
- `DeviceInfo` struct
- `EnumerateDevices` operator (proves device discovery works)

### Phase 2 — Image acquisition
- `GenTLDataStream` with buffer loop + `EventGetData` wait
- `IplImage` construction from buffer metadata
- `GenICamCapture` operator

### Phase 3 — Feature node access
- `NodeMap` XML fetch and parse
- `NodeTypes` (Integer, Float, String, Boolean, Enumeration, Command)
- `FeatureValue` union type
- `GetFeatureNode` + `SetFeatureNode` operators

---

## Verification

1. **Unit**: `EnumerateDevices` with a real GenTL producer (e.g. pylon's `PylonGigE.cti` or `PylonUsb.cti`) lists cameras
2. **Integration**: `GenICamCapture` subscribes and frames arrive; frame dimensions match camera config
3. **Feature nodes**: `GetFeatureNode` on `"ExposureTime"` returns current exposure; `SetFeatureNode` changes it and the next frame reflects the change
4. **Resource cleanup**: subscribing + disposing repeatedly does not leak handles or memory
5. **No-camera path**: `EnumerateDevices` with no camera returns empty array without exception
