# Bonsai.GenICam

A [Bonsai](https://bonsai-rx.org) package for acquiring images and reading/writing features from any GenICam/GenTL-compliant camera — USB3 Vision, GigE Vision, CoaXPress — without a proprietary SDK.

## Prerequisites

- Windows x64
- [.NET Framework 4.7.2](https://dotnet.microsoft.com/download/dotnet-framework/net472)
- A **GenTL producer** (`.cti` file) from your camera vendor, e.g.:
  - Basler Pylon — `PylonUsb.cti` / `PylonGigE.cti`
  - IDS — `idsGenTL.cti`
  - FLIR Spinnaker — `FLIR_GenTL_v3_4.cti`
  - Allied Vision Vimba — `VimbaUSBTL.cti`
- The `GENICAM_GENTL64_PATH` environment variable must point to the folder containing the `.cti` file(s)

## Bonsai Operators

| Operator | Type | Description |
|---|---|---|
| `GenICamCapture` | `Source<IplImage>` | Streams frames from a camera |
| `EnumerateDevices` | `Source<DeviceInfo[]>` | Lists all detected GenICam cameras |
| `GetFeatureNode` | `Source<FeatureValue>` | Reads a named feature (e.g. `ExposureTime`) |
| `SetFeatureNode` | `Combinator` | Writes a named feature on each upstream element |

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
- `DeviceIndex` — camera index (default `0`)
- `NumBuffers` — acquisition buffer count (default `4`)
- `FrameTimeoutMs` — per-frame timeout in ms (default `5000`)

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
.\src\Bonsai.GenICam.TestApp\bin\Release\net472\win-x64\Bonsai.GenICam.TestApp.exe [device-index]
```

`device-index` selects which camera to use for feature listing and frame capture (defaults to `1`).

### What it does

1. **Enumerates** all GenTL cameras and prints vendor/model/serial
2. **Extracts GenICam XML** from every detected camera and saves each file to `example-camera-xml/` next to the exe
3. **Lists all readable features** for the target device
4. **Captures 5 frames** from the target device and prints dimensions

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
  Frame 1: 1920x1200  depth=IPL_DEPTH_8U  ch=1
  Frame 2: 1920x1200  depth=IPL_DEPTH_8U  ch=1
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

**GenAPI NodeMap** (`src/Bonsai.GenICam/GenApi/`) — Fetches the device XML via `GCReadPort`, parses it, and exposes named feature nodes. Supports the six node types covering ~95% of real-world devices: Integer, Float, String, Boolean, Enumeration, Command.

## License

MIT
