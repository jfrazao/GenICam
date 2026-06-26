# Test suites

The project has two complementary test surfaces:

| Suite | Project | Needs a camera? | Where it runs |
|---|---|---|---|
| **Offline tests** | `Bonsai.GenICam.Tests` (xUnit) | No | `dotnet test` — locally **and** in CI |
| **Hardware test** | `Bonsai.GenICam.LocalGenTLUnitTest` (console) | Yes | run by hand against a connected camera |

You can run **both locally**: `dotnet test` for the offline suite, and the console app for the hardware suite.

---

## Offline tests — `Bonsai.GenICam.Tests`

Deterministic, hardware-free xUnit tests that run on every `dotnet test` (and in CI):

```powershell
dotnet test src/Bonsai.GenICam.Tests/Bonsai.GenICam.Tests.csproj -c Release
```

- **Chunk decode** (one case per saved camera XML fixture) — builds a `NodeMap` from each fixture in `testedCameraXml/` and decodes synthetic chunk bytes through `TryReadChunk`, exercising the chunk-ID map and the typed decode path (register → mask/sign/endian → SwissKnife/Converter). Fixtures with no `<ChunkID>` ports pass vacuously.
- **Message dispatch (#13)** — a rejected read, write, or startup override yields a `GenICamMessageType.Error` message rather than throwing or faulting the stream (exercised against a fixture `NodeMap`, no camera).

---

## Hardware test — `Bonsai.GenICam.LocalGenTLUnitTest`

A console tool for verifying your GenTL setup end-to-end with a real camera (no Bonsai required).

### Build

```powershell
dotnet build src/Bonsai.GenICam.LocalGenTLUnitTest/Bonsai.GenICam.LocalGenTLUnitTest.csproj -c Release
```

### Run

```powershell
# From the project root:
.\artifacts\bin\Bonsai.GenICam.LocalGenTLUnitTest\release_win-x64\Bonsai.GenICam.LocalGenTLUnitTest.exe [device-index] [sn=<serial>]
```

- `device-index` — selects which camera to use for feature listing and capture (defaults to `1`).
- `sn=<serial>` — pins the `GenICamDevice` steps to a specific camera by serial number. **Recommended when more than one GenTL producer is installed:** the global device index can resolve to a *different* physical camera on each open, so index-based selection is not stable across producers (see the README camera-selection section). Pin by serial to test one specific camera reliably.

### What it does

1. **Enumerates** all GenTL cameras and prints vendor/model/serial — verifies producer loading and device discovery
2. **Extracts GenICam XML** from every detected camera and saves each file as `<Model>.xml` in `testedCameraXml/` next to the exe — verifies `GCReadPort` and XML parsing
3. **Lists all readable features** for the target device — verifies the GenAPI NodeMap across all node types
4. **Write/readback round-trip test** for `ExposureTime` and `Gain` — writes a test value, reads it back, then restores the original; verifies Converter formula evaluation and the write path
5. **Shared connection — capture + chunk data + feature round-trip** — opens **one** `GenICamDevice` (`AcquireFrames = true`, `ChunkModeActive = true`) and drives everything through a **single `Process()` subscription**: it captures 5 frames, prints the typed `GenICamFrame.ChunkData`, and concurrently runs a `Read`/`Write`/`Read`/`Write`(restore)/`Read` feature round-trip on the same connection — plus a `#13` probe that pushes a rejected write through the live bus and confirms it returns an `Error` without faulting the stream. This mirrors how a real Bonsai workflow keeps one connection open (capture + feature I/O reusing the live `NodeMap`) rather than opening a competing connection per operation — important because some producers (notably the IDS uEye) can fail to find or release the device between rapid independent opens. The test sets the internal `EnableAllChunks` seam so the device enables every chunk selector the camera exposes (before `AcquisitionStart`).

Running it successfully end-to-end confirms that GenTL producer loading, device enumeration, feature access, frame acquisition, chunk-data decoding, and the message-bus dispatch path all work with your camera and driver.

> **Note on chunk testing.** `EnableAllChunks` is an `internal` test-only seam — it is *not* a workflow property. Real workflows leave it off and configure chunks on the camera (e.g. via a saved UserSet); `GenICamDevice` then decodes whatever chunks the producer embeds in each buffer. The offline chunk-decode tests (`Bonsai.GenICam.Tests`) need no camera and are the authoritative correctness check; the live capture (step 5) is a hardware integration smoke test whose available chunks depend on the camera/producer.

> **Note on device selection.** With multiple GenTL producers installed, the global device index is not stable across opens — pass `sn=<serial>` to pin the `GenICamDevice` steps to one physical camera (see [Run](#run)).

### Example output

```
=== Bonsai.GenICam hardware test ===
(Offline, hardware-free tests live in Bonsai.GenICam.Tests — run `dotnet test`.)

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
  Write round-trip: PASS
  Restore verify  : PASS
  #13 probe (rejected write through live bus): 1 Error message(s) received, stream survived: yes : PASS
  Shared-connection test complete — one open connection served frames, chunks, and feature I/O.
```

## Tested-camera XML fixtures

`src/Bonsai.GenICam.LocalGenTLUnitTest/testedCameraXml/` contains GenICam XML extracted from real cameras, named `<Model>.xml` (no device index). The build copies them next to both the hardware exe and the offline test assembly, so the offline chunk-decode tests (`Bonsai.GenICam.Tests`) run with no camera attached.

Cameras run through the full hardware suite live pass capture, write/readback, and chunk data. The only failure is the message-bus round-trip on cameras that report `ExposureTime` in seconds (Point Grey Flea3 / Chameleon3): the test computes `round(original × 1.1, 2)`, which rounds the camera's ~1e-8 s value down to `0` and the camera rejects the write — a **test-harness rounding quirk, not a code bug** (the dedicated write/readback step, which picks a value within the feature's limits, passes).

| Camera | Fixture | Offline decode | Capture | Write/readback | Live chunk | Message-bus |
|---|---|---|---|---|---|---|
| FLIR Blackfly S BFS-U3-16S2M | `Blackfly_S_BFS-U3-16S2M.xml` | ✓ 14 chunks | ✓ | ✓ | ✓ | ✓ |
| FLIR Blackfly S BFS-U3-63S4M | `Blackfly_S_BFS-U3-63S4M.xml` | ✓ 15 chunks | ✓ | ✓ | ✓ | ✓ |
| Point Grey Chameleon3 CM3-U3-13Y3M | `Chameleon3_CM3-U3-13Y3M.xml` | ✓ 15 chunks | ✓ | ✓ | ✓ | ⚠️ quirk |
| Point Grey Flea3 FL3-U3-13S2M | `Flea3_FL3-U3-13S2M.xml` | ✓ 14 chunks | ✓ | ✓ | ✓ | ⚠️ quirk |
| HIKVISION MV-CA013-A0UM | `MV-CA013-A0UM.xml` | ✓ no chunks | ✓ | ✓ | n/a | ✓ |
| IDS UI-3220CP-M | `UI322xCP-M.xml` | ✓ no chunks | ✓ | ✓ | n/a | ✓ |
| Allied Vision Mako U-029B | `Mako_U-029B.xml` | ✓ no chunks | ✓ | ✓ | n/a | ✓ |

Legend: ✓ pass · ⚠️ quirk = harness rounding issue on seconds-unit `ExposureTime`, not a code bug (see above) · n/a = camera exposes no chunk data. All cameras are USB3 Vision. The "Offline decode" column is covered by `Bonsai.GenICam.Tests` (one case per fixture); the rest are the hardware suite.

To add a camera, run the hardware tool with it connected: the extraction step (step 2) writes `testedCameraXml/<Model>.xml` next to the exe — copy that file into `src/Bonsai.GenICam.LocalGenTLUnitTest/testedCameraXml/` to include it as a fixture (the offline tests pick it up automatically).
