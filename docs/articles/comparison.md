# Bonsai.GenICam — Comparison with Other Bonsai Camera Packages

Comparison of `Bonsai.GenICam` design decisions against peer Bonsai camera integrations:
[bonsai-rx/spinnaker](https://github.com/bonsai-rx/spinnaker),
[bonsai-rx/pylon](https://github.com/bonsai-rx/pylon),
[bonsai-rx/vimba](https://github.com/bonsai-rx/vimba),
and also Ximea and Teledyne DALSA Sapera packages.

## Threading model

| Package | Approach |
|---|---|
| `Bonsai.GenICam` | `new Thread()` + `thread.Join(5000)` on dispose |
| Spinnaker, Pylon, Vimba, Ximea, Sapera | `Task.Factory.StartNew(..., LongRunning)` |

`LongRunning` is a thread-pool hint to use a dedicated OS thread — functionally equivalent to an explicit `Thread`. No practical difference in behaviour.

## Frame delivery

| Package | Pattern |
|---|---|
| `Bonsai.GenICam` | Blocking `EventGetData` loop (GenTL `EVENT_NEW_BUFFER`) → `OnNext` |
| Spinnaker, Vimba | Registered frame callback / event handler |
| Pylon | `StreamGrabber.ImageGrabbed` event subscription |
| Ximea | Polling (`GetXI_IMG`) |

The blocking GenTL event loop is the idiomatic approach for GenTL consumers and is equivalent in structure to the callback-based approaches used by vendor SDKs.

## Camera selection

| Package | Mechanisms |
|---|---|
| `Bonsai.GenICam` | `ProducerPath` + `DeviceIndex` + `CameraModel` + `SerialNumber` (priority hierarchy) |
| Spinnaker | `Index` or `SerialNumber` |
| Pylon | `SerialNumber` only |
| Vimba | `Index` or `SerialNumber` |

`Bonsai.GenICam` has the richest selection mechanism, appropriate since it wraps arbitrary GenTL producers across vendors rather than a single vendor SDK.

## Startup feature overrides

| Package | Approach |
|---|---|
| `Bonsai.GenICam` | `FeatureConfiguration` — full UI editor, per-feature overrides, persisted in `.bonsai`, auto-reset when camera model changes |
| Pylon | Load a `.pfs` parameter file (`ParameterFile` property) |
| Spinnaker, Vimba | `virtual Configure()` override point for subclasses |
| Ximea | Typed properties (`Exposure`, `Gain`, `ImageFormat`) |
| Sapera | Typed properties with min/max clamping validation |

`Bonsai.GenICam`'s approach is the most ergonomic for end users — no subclassing, no external files, live editing in the Bonsai IDE.

## Output type

| Package | Output |
|---|---|
| `Bonsai.GenICam` | `GenICamFrame` — carries `IplImage` + `TimestampNs` + `FrameId` + `IsIncomplete`; use `.Image` for Bonsai Vision pipelines |
| Spinnaker, Pylon, Vimba | Vendor `DataFrames` (raw image + metadata; needs a downstream conversion step) |
| Ximea, Sapera | Vendor `DataFrames` |

`GenICamFrame` carries richer metadata (timestamp, frame ID, incomplete flag) alongside the `IplImage`. Vendor packages return proprietary `DataFrame` types that need a downstream conversion step for Bonsai Vision.

## Unique aspects of Bonsai.GenICam

- **No vendor SDK** — pure dynamic P/Invoke onto `.cti` producers; the only Bonsai camera package that is vendor-agnostic at the binary level.
- **Own GenAPI NodeMap** — all other packages delegate feature access to the vendor SDK's managed GenAPI layer; `Bonsai.GenICam` parses and evaluates the GenICam XML itself (`GenApi/NodeMap.cs`, `NodeTypes.cs`).
- **`HandleProcessCorruptedStateExceptions`** on the capture thread — necessary when calling arbitrary native `.cti` code that may raise SEH exceptions; none of the other packages do this explicitly.
- **Design-time live connection** (`IGenICamSource` / `LiveNodeMap`) — the feature editor opens a real device connection at design time to populate the feature grid and validate writes; the other packages have no equivalent.
