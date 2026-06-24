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

1. **Offline chunk decode** — runs first, with no camera attached. Builds a `NodeMap` from each saved XML fixture in `testedCameraXml/` (copied next to the exe), prints the discovered chunk-ID → feature map, and decodes synthetic chunk bytes through `TryReadChunk`; verifies the Port-based chunk-ID resolution and the typed decode path (register → mask/sign/endian → SwissKnife/Converter) deterministically across every fixture
2. **Enumerates** all GenTL cameras and prints vendor/model/serial — verifies producer loading and device discovery
3. **Extracts GenICam XML** from every detected camera and saves each file as `<Model>.xml` in `testedCameraXml/` next to the exe — verifies `GCReadPort` and XML parsing
4. **Lists all readable features** for the target device — verifies the GenAPI NodeMap across all node types
5. **Write/readback round-trip test** for `ExposureTime` and `Gain` — writes a test value, reads it back, then restores the original; verifies Converter formula evaluation and the write path
6. **Captures 5 frames via `GenICamDevice`** — creates a `GenICamDevice` with `AcquireFrames = true`, subscribes to the output, filters `Frame`-type messages, and prints frame dimensions; verifies the acquisition loop and `GenICamFrame` construction
7. **Chunk mode (live capture)** — captures 5 frames with `ChunkModeActive = true` and prints the typed `GenICamFrame.ChunkData` for each. The test sets the internal `EnableAllChunks` seam so the device enables every chunk selector the camera exposes (on the acquisition connection, before `AcquisitionStart`); verifies end-to-end chunk delivery and decoding against a live camera (e.g. `ChunkFrameID` incrementing, `ChunkTimestamp` monotonic)
8. **Message-bus feature round-trip** — sends a sequence of `ReadRequest`, `WriteRequest`, `ReadRequest`, `WriteRequest` (restore), `ReadRequest` messages through a single `GenICamDevice` subscription (`AcquireFrames = false`); checks that each readback matches the written value within 1-unit tolerance

Running it successfully end-to-end confirms that GenTL producer loading, device enumeration, feature access, frame acquisition, chunk-data decoding, and the message-bus dispatch path all work with your camera and driver.

> **Note on chunk testing.** `EnableAllChunks` is an `internal` test-only seam — it is *not* a workflow property. Real workflows leave it off and configure chunks on the camera (e.g. via a saved UserSet); `GenICamDevice` then decodes whatever chunks the producer embeds in each buffer. The offline chunk decode (step 1) needs no camera and is the authoritative correctness check; the live capture (step 7) is a hardware integration smoke test whose available chunks depend on the camera/producer.

## Example output

```
=== Bonsai.GenICam Test ===

=== Chunk decode (offline, from saved XML fixtures) ===

--- Blackfly_S_BFS-U3-63S4M.xml ---
  Chunk features discovered: 15
    0x004CE783  ChunkFrameID                 => ...  [Int64]
    0x0508000E  ChunkTimestamp               => ...  [Int64]
    ...
  Decoded 15/15 chunk feature(s) from synthetic bytes
--- UI322xCP-M.xml ---
  Chunk features discovered: 0
  (no <ChunkID> ports — this camera exposes no chunk data)

Enumerating GenICam devices...
Found 2 device(s):
  [0] FLIR Blackfly S BFS-U3-63S4M s/n=22045106
  [1] IDS UI-3220CP-M s/n=4104084462

=== Extracting GenICam XML from all cameras ===

--- Camera 0: FLIR Blackfly S BFS-U3-63S4M (S/N: 22045106) ---
XML length: 885594 bytes
Saved to: ...\testedCameraXml\Blackfly_S_BFS-U3-63S4M.xml

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

=== Chunk mode (live capture, ChunkModeActive=true) ===
  Frame 1: 7 chunk field(s)
      ChunkExposureTime            = 40000  [Int64]
      ChunkFrameID                 = 1  [Int64]
      ChunkTimestamp               = 20009169200  [Int64]
      ...
  Done — 5 frame(s), 5 carried chunk data.

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

## Tested-camera XML fixtures

`src/Bonsai.GenICam.LocalGenTLUnitTest/testedCameraXml/` contains GenICam XML extracted from real cameras, named `<Model>.xml` (no device index). The build copies them next to the exe so the offline chunk-decode step (step 1) runs with no camera attached. Cameras with `<ChunkID>` ports exercise chunk decoding; the others verify the parser handles cameras without chunk data.

| File | Camera | Chunk data |
|---|---|---|
| `Blackfly_S_BFS-U3-16S2M.xml` | FLIR Blackfly S BFS-U3-16S2M (USB3 Vision) | 14 chunks |
| `Blackfly_S_BFS-U3-63S4M.xml` | FLIR Blackfly S BFS-U3-63S4M (USB3 Vision) | 15 chunks |
| `Chameleon3_CM3-U3-13Y3M.xml` | Point Grey Chameleon3 CM3-U3-13Y3M (USB3 Vision) | 15 chunks |
| `Flea3_FL3-U3-13S2M.xml` | Point Grey Flea3 FL3-U3-13S2M (USB3 Vision) | 14 chunks |
| `MV-CA013-A0UM.xml` | HIKVISION MV-CA013-A0UM (USB3 Vision) | none |
| `UI322xCP-M.xml` | IDS UI-3220CP-M (USB3 Vision) | none |

To add a camera, run the tool with it connected: the extraction step (step 3) writes `testedCameraXml/<Model>.xml` next to the exe — copy that file into `src/Bonsai.GenICam.LocalGenTLUnitTest/testedCameraXml/` to include it as a fixture.
