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
5. **Captures 5 frames via `GenICamDevice`** — creates a `GenICamDevice` with `AcquireFrames = true`, subscribes to the output, filters `Frame`-type messages, and prints frame dimensions; verifies the acquisition loop and `GenICamFrame` construction
6. **Message-bus feature round-trip** — sends a sequence of `ReadRequest`, `WriteRequest`, `ReadRequest`, `WriteRequest` (restore), `ReadRequest` messages through a single `GenICamDevice` subscription (`AcquireFrames = false`); checks that each readback matches the written value within 1-unit tolerance

Running it successfully end-to-end confirms that GenTL producer loading, device enumeration, feature access, frame acquisition, and the message-bus dispatch path all work with your camera and driver.

## Example output

```
=== Bonsai.GenICam Test ===

Enumerating GenICam devices...
Found 2 device(s):
  [0] FLIR Blackfly S BFS-U3-16S2M s/n=12345678
  [1] IDS UI-3220CP-M s/n=4104084462

=== Extracting GenICam XML from all cameras ===

--- Camera 0: FLIR Blackfly S BFS-U3-16S2M (S/N: 12345678) ---
XML length: 1085764 bytes
Saved to: ...\example-camera-xml\camera_0_Blackfly_S_BFS-U3-16S2M.xml

All readable features of device 0:
  DeviceVendorName = FLIR
  ExposureTime = 10000
  Gain = 0
  ...

=== Write/Readback round-trip test (ExposureTime, Gain) ===
  ExposureTime:
    Kind=Float  Rep=Linear  Unit=us
    Limits: min=6  max=500000  step=none
    Before: 10000  Written: 15000  Readback: 15000  Error: none
  ...

Capturing 5 frames from device 0 via GenICamDevice...
  Frame 1: 1440x1080  depth=U8  ch=1
  ...
  Done — 5 frame(s) received.

=== GenICamDevice: message-bus feature round-trip ===
  Initial read: ReadResponse(ExposureTime=10000)
  [0] read before write : ReadResponse(ExposureTime=10000)
  [1] write 11000       : WriteAck(ExposureTime=11000)
  [2] readback after write: ReadResponse(ExposureTime=11000)
  [3] restore 10000     : WriteAck(ExposureTime=10000)
  [4] readback after restore: ReadResponse(ExposureTime=10000)
  Write round-trip: PASS
  Restore verify  : PASS
  Message-bus round-trip: PASSED.
```

## Example XML files

`src/Bonsai.GenICam.LocalGenTLUnitTest/example-camera-xml/` contains GenICam XML extracted from three real cameras for reference:

| File | Camera |
|---|---|
| `camera_0_Blackfly_S_BFS-U3-16S2M.xml` | FLIR Blackfly S (USB3 Vision) |
| `camera_0_UI322xCP-M.xml` | IDS UI-3220CP-M (USB3 Vision) |
| `camera_1_MV-CA013-A0UM.xml` | HIKVISION MV-CA013-A0UM (USB3 Vision) |
