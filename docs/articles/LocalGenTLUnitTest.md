# Running the LocalGenTLUnitTest

`Bonsai.GenICam.LocalGenTLUnitTest` is a console tool for verifying your GenTL setup without Bonsai.

## Build

```powershell
dotnet build src/Bonsai.GenICam.LocalGenTLUnitTest/Bonsai.GenICam.LocalGenTLUnitTest.csproj -c Release
```

## Run

```powershell
# From the project root:
.\artifacts\bin\Bonsai.GenICam.LocalGenTLUnitTest\release_win-x64\Bonsai.GenICam.LocalGenTLUnitTest.exe [device-index]
```

`device-index` selects which camera to use for feature listing and frame capture (defaults to `1`).

## What it does

1. **Enumerates** all GenTL cameras and prints vendor/model/serial — verifies producer loading and device discovery
2. **Extracts GenICam XML** from every detected camera and saves each file to `example-camera-xml/` next to the exe — verifies `GCReadPort` and XML parsing
3. **Lists all readable features** for the target device — verifies the GenAPI NodeMap across all node types
4. **Write/readback round-trip test** for `ExposureTime` and `Gain` — writes a test value, reads it back, then restores the original; verifies Converter formula evaluation and the write path
5. **Captures 5 frames** from the target device and prints `GenICamFrame` dimensions — verifies the buffer acquisition loop and frame wrapping
6. **Connection-sharing test 1** — runs `GenICamCapture` (name `"test1"`) and `GetFloatFeature` (connection `"test1"`) concurrently; expects 3 frames and 3 `ExposureTime` reads — verifies `BehaviorSubject` handoff and shared-connection feature reads
7. **Connection-sharing test 2** — reads `ExposureTime` before, writes 20000 µs via `SetFloatFeature`, reads back — verifies the write path through a shared connection

Running it successfully end-to-end confirms that GenTL producer loading, device enumeration, feature access, image acquisition, and connection sharing all work with your camera and driver.

## Example output

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

=== Connection sharing: concurrent capture + feature read ===
  [Capture] Frame 1: 1920x1200
  [Reader]  ExposureTime = 10000
  [Capture] Frame 2: 1920x1200
  [Reader]  ExposureTime = 10000
  [Capture] Frame 3: 1920x1200
  [Capture] Done — 3 frame(s)
  [Reader]  ExposureTime = 10000
  [Reader]  Done — 3 read(s)
  Connection-sharing test 1: PASS

=== Connection sharing: SetFloatFeature write+verify ===
  Before: 10000  Written: 20000  After: 20000
  Connection-sharing test 2: PASS
```

## Example XML files

`src/Bonsai.GenICam.LocalGenTLUnitTest/example-camera-xml/` contains GenICam XML extracted from three real cameras for reference:

| File | Camera |
|---|---|
| `camera_0_Blackfly_S_BFS-U3-16S2M.xml` | Basler Blackfly S (USB3 Vision) |
| `camera_0_UI322xCP-M.xml` | IDS UI-3220CP-M (USB3 Vision) |
| `camera_1_MV-CA013-A0UM.xml` | HIKVISION MV-CA013-A0UM (USB3 Vision) |
