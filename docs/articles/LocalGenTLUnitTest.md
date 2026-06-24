# Running the LocalGenTLUnitTest

`Bonsai.GenICam.LocalGenTLUnitTest` is a console tool for verifying your GenTL setup without Bonsai.

## Build

```powershell
dotnet build src/Bonsai.GenICam.LocalGenTLUnitTest/Bonsai.GenICam.LocalGenTLUnitTest.csproj -c Release
```

## Run

```powershell
# From the project root:
.\artifacts\bin\Bonsai.GenICam.LocalGenTLUnitTest\release_win-x64\Bonsai.GenICam.LocalGenTLUnitTest.exe [device-index] [sn=<serial>]
```

- `device-index` — selects which camera to use for feature listing and capture (defaults to `1`).
- `sn=<serial>` — pins the `GenICamDevice` steps to a specific camera by serial number. **Recommended when more than one GenTL producer is installed:** the global device index can resolve to a *different* physical camera on each open, so index-based selection is not stable across producers. Pin by serial to test one specific camera reliably.

## What it does

1. **Offline chunk decode** — runs first, with no camera attached. Builds a `NodeMap` from each saved XML fixture in `testedCameraXml/` (copied next to the exe), prints the discovered chunk-ID → feature map, and decodes synthetic chunk bytes through `TryReadChunk`; verifies the Port-based chunk-ID resolution and the typed decode path (register → mask/sign/endian → SwissKnife/Converter) deterministically across every fixture
2. **Enumerates** all GenTL cameras and prints vendor/model/serial — verifies producer loading and device discovery
3. **Extracts GenICam XML** from every detected camera and saves each file as `<Model>.xml` in `testedCameraXml/` next to the exe — verifies `GCReadPort` and XML parsing
4. **Lists all readable features** for the target device — verifies the GenAPI NodeMap across all node types
5. **Write/readback round-trip test** for `ExposureTime` and `Gain` — writes a test value, reads it back, then restores the original; verifies Converter formula evaluation and the write path
6. **Shared connection — capture + chunk data + feature round-trip** — opens **one** `GenICamDevice` (`AcquireFrames = true`, `ChunkModeActive = true`) and drives everything through a **single `Process()` subscription**: it captures 5 frames, prints the typed `GenICamFrame.ChunkData`, and concurrently runs a `Read`/`Write`/`Read`/`Write`(restore)/`Read` feature round-trip on the same connection. This mirrors how a real Bonsai workflow keeps one connection open (capture + feature nodes reusing the live `NodeMap`) rather than opening a competing connection per operation — important because some producers (notably the IDS uEye) can fail to find or release the device between rapid independent opens. The test sets the internal `EnableAllChunks` seam so the device enables every chunk selector the camera exposes (before `AcquisitionStart`); verifies end-to-end frame acquisition, chunk delivery/decoding (e.g. `ChunkFrameID` incrementing, `ChunkTimestamp` monotonic), and feature read/write on a single shared connection

Running it successfully end-to-end confirms that GenTL producer loading, device enumeration, feature access, frame acquisition, chunk-data decoding, and the message-bus dispatch path all work with your camera and driver.

> **Note on chunk testing.** `EnableAllChunks` is an `internal` test-only seam — it is *not* a workflow property. Real workflows leave it off and configure chunks on the camera (e.g. via a saved UserSet); `GenICamDevice` then decodes whatever chunks the producer embeds in each buffer. The offline chunk decode (step 1) needs no camera and is the authoritative correctness check; the live capture (step 6) is a hardware integration smoke test whose available chunks depend on the camera/producer.

> **Note on device selection.** With multiple GenTL producers installed, the global device index is not stable across opens — pass `sn=<serial>` to pin the `GenICamDevice` steps to one physical camera (see [Run](#run)).

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

=== GenICamDevice: shared connection (capture + chunk + feature round-trip) ===
  Frames: 5 received, 5 carried chunk data.
      ChunkExposureTime            = 40000  [Int64]
      ChunkFrameID                 = 1  [Int64]
      ChunkTimestamp               = 20009169200  [Int64]
      ...
  Feature round-trip on the SAME connection: 3 read(s), 2 write(s)
    ReadResponse(ExposureTime=10000)
    WriteAck(ExposureTime=11000)
    ReadResponse(ExposureTime=11000)
    WriteAck(ExposureTime=10000)
    ReadResponse(ExposureTime=10000)
  Write round-trip: PASS
  Restore verify  : PASS
  Shared-connection test complete — one open connection served frames, chunks, and feature I/O.
```

## Tested-camera XML fixtures

`src/Bonsai.GenICam.LocalGenTLUnitTest/testedCameraXml/` contains GenICam XML extracted from real cameras, named `<Model>.xml` (no device index). The build copies them next to the exe so the offline chunk-decode step (step 1) runs with no camera attached.

Cameras run through the full suite live pass capture, write/readback, and chunk data. The only failure is the message-bus round-trip on cameras that report `ExposureTime` in seconds (Point Grey Flea3 / Chameleon3): the test computes `round(original × 1.1, 2)`, which rounds the camera's ~1e-8 s value down to `0` and the camera rejects the write — a **test-harness rounding quirk, not a code bug** (the dedicated write/readback step, which picks a value within the feature's limits, passes).

| Camera | Fixture | Offline decode | Capture | Write/readback | Live chunk | Message-bus |
|---|---|---|---|---|---|---|
| FLIR Blackfly S BFS-U3-16S2M | `Blackfly_S_BFS-U3-16S2M.xml` | ✓ 14 chunks | ✓ | ✓ | ✓ | ✓ |
| FLIR Blackfly S BFS-U3-63S4M | `Blackfly_S_BFS-U3-63S4M.xml` | ✓ 15 chunks | ✓ | ✓ | ✓ | ✓ |
| Point Grey Chameleon3 CM3-U3-13Y3M | `Chameleon3_CM3-U3-13Y3M.xml` | ✓ 15 chunks | ✓ | ✓ | ✓ | ⚠️ quirk |
| Point Grey Flea3 FL3-U3-13S2M | `Flea3_FL3-U3-13S2M.xml` | ✓ 14 chunks | ✓ | ✓ | ✓ | ⚠️ quirk |
| HIKVISION MV-CA013-A0UM | `MV-CA013-A0UM.xml` | ✓ no chunks | ✓ | ✓ | n/a | ✓ |
| IDS UI-3220CP-M | `UI322xCP-M.xml` | ✓ no chunks | ✓ | ✓ | n/a | ✓ |

Legend: ✓ pass · ⚠️ quirk = harness rounding issue on seconds-unit `ExposureTime`, not a code bug (see above) · n/a = camera exposes no chunk data · — offline-decode fixture only (not re-tested live). All cameras are USB3 Vision.

To add a camera, run the tool with it connected: the extraction step (step 3) writes `testedCameraXml/<Model>.xml` next to the exe — copy that file into `src/Bonsai.GenICam.LocalGenTLUnitTest/testedCameraXml/` to include it as a fixture.
