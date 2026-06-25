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
| `GenICamDevice` | `Combinator<GenICamMessage, GenICamMessage>` | Single-owner device hub: routes feature read/write messages and (when acquiring) emits frames on the same stream |
| `EnumerateDevices` | `Source<DeviceInfo[]>` | Lists all detected GenICam devices |
| `ListFeatureValues` | `Source<FeatureValue[]>` | Reads all accessible features from a device as a one-shot array snapshot |
| `CreateReadMessage` | `Transform` | Turns each upstream element into a read-request message for a named feature |
| `CreateWriteMessage` | `Transform` | Turns each upstream value into a write-request message for a named feature |
| `FilterMessage` | `Combinator<GenICamMessage, GenICamMessage>` | Filters the message stream by feature name and/or message type |
| `ParseFeature` | `Transform` | Extracts a named feature value from read responses as a strongly typed output edge |
| `ParseChunk` | `Transform` | Extracts a named chunk-data field from each frame as a strongly typed output edge |

### The message model

Every camera interaction is a `GenICamMessage` flowing through a single `GenICamDevice` node. There is no separate capture node, no per-feature get/set node, and no implicit connection-sharing registry — one `GenICamDevice` owns the camera for its subscription lifetime, and reads, writes, and frames all travel on its one observable stream.

A `GenICamMessage` carries a `Type` (`ReadRequest`, `WriteRequest`, `ReadResponse`, `WriteAck`, `Error`, `Frame`), a `FeatureName`, an optional `Payload` string, and — for `Frame` messages — a `GenICamFrame`. `GenICamDevice` consumes requests from its upstream source and emits the matching responses, acks, and frames downstream.

A read or write the device rejects (a feature the camera does not expose, a value out of range, a read-only node) is **not** fatal: `GenICamDevice` emits an `Error` message (with the feature name and the error text in `Payload`) and keeps the stream alive, rather than terminating it. Rejected **startup overrides** (`Features`) surface the same way. To react to failures, route them with `FilterMessage(MessageType=Error)`; to ignore them, do nothing — `ParseFeature`/`ParseChunk` only match their own responses and skip `Error` messages.

Canonical chains:

```
# Read a feature as a typed value
Source ─► CreateReadMessage(ExposureTime) ─► GenICamDevice ─► ParseFeature(ExposureTime, Float) ─► double

# Write a feature
Source(double) ─► CreateWriteMessage(ExposureTime) ─► GenICamDevice ─► (WriteAck)

# Acquire frames (and read chunk metadata)
GenICamDevice(AcquireFrames=true) ─┬─► FilterMessage(Frame) ─► (GenICamFrame / image)
                                   └─► ParseChunk(ChunkFrameID, Integer) ─► long
```

`FilterMessage` and `ParseFeature`/`ParseChunk` both match on feature name internally, so you can fan multiple read/parse pairs off one `GenICamDevice` and each extracts only its own messages.

### GenICamDevice

The hub operator. Subscribe a request stream into it and it emits responses, acks, and (optionally) frames.

- `ProducerPath` — optional path to a specific `.cti` file (leave blank to use `GENICAM_GENTL64_PATH`)
- `DeviceIndex` — zero-based camera index; when `CameraModel` is set, counts only within the cameras matching that model (default `0`)
- `CameraModel` — optional vendor+model string (e.g. `Basler Blackfly S BFS-U3-16S2M`); click the dropdown to pick from all detected cameras. When set, selection uses model name + `DeviceIndex` rather than global index.
- `SerialNumber` — optional serial number; click the dropdown to pick from all detected cameras. When set, takes priority over `CameraModel` and `DeviceIndex`. Use this to pin a workflow to one specific physical camera.
- `NumBuffers` — acquisition buffer count (default `4`)
- `FrameTimeoutMs` — per-frame `EventGetData` timeout in ms (default `5000`). Normal teardown uses `EventKill` and does not rely on this value; it is a safety net for producers that do not implement `EventKill` correctly.
- `AcquireFrames` — when `true` (default), runs the acquisition loop and emits `Frame` messages alongside feature responses; set `false` for feature-only access without streaming
- `ChunkModeActive` — when `true`, enables GenICam chunk mode so each buffer carries per-frame metadata in `GenICamFrame.ChunkData` (default `false`). Requires producer support for `DSGetBufferChunkData` (GenTL 1.5+); `ChunkData` is `null` on every frame if unsupported.
- `Features` — list of feature overrides applied at startup; double-click the operator (or click `...` on the property) to open the feature editor

#### Camera selection priority

At workflow start, the device is selected in this order:

1. **SerialNumber set** — search all producers (or the configured `ProducerPath`) for that exact serial; error if not found
2. **CameraModel set** — search producers, filter by `"Vendor Model"` string, pick by `DeviceIndex` within the matching set; error if no match or index out of range
3. **Neither** — global `DeviceIndex` across all producers (the default behaviour)

> **Multi-producer caveat:** when more than one GenTL producer is installed, the global device index is not stable across opens — it can resolve to a different physical camera each time. Pin by `SerialNumber` to test or run against one specific camera reliably.

#### Feature overrides

The `Features` property stores a flat list of named feature values applied to the camera before acquisition starts. Open the editor by double-clicking the `GenICamDevice` operator (or clicking `...` on the `Features` property).

**Storage** — serialized as `<Feature>` elements inside the workflow `.bonsai` file:

```xml
<Features>
  <Feature name="ExposureTime" value="10000" />
  <Feature name="Gain" value="0" />
</Features>
```

Persisted by Bonsai's normal **Save workflow** (Ctrl+S).

> **Note:** the override list is **not** cleared automatically when you change `CameraModel`, `DeviceIndex`, or `SerialNumber` — it persists verbatim until you edit it in the feature editor. This lets you build model-agnostic workflows that carry a common set of overrides (e.g. `ExposureTime`, `Gain`, `PixelFormat`) across different cameras: at startup each override is written best-effort and any feature the target camera does not expose is silently skipped. The trade-off is that a stale override meant for a different model is also skipped silently rather than flagged — if a setting "doesn't apply," check that the feature exists on the connected camera.

**In the editor**

- The grid shows the device's features grouped by category, with current live values and a **Startup** column (checkbox) marking which features are in the override list. A visibility filter (All / Beginner / Expert / Guru) controls how many features are shown; `Invisible` features are always hidden.
- It connects to the **live** node map when the workflow is running, or opens a **design-time** connection otherwise. The feature and category description panes show the GenICam XML documentation for the selected node.
- **Editing a value while connected** — written immediately to the camera; the value read back is stored in the override list and the Startup checkbox is ticked automatically. Enumerations use a dropdown, integers/floats a spin box with a min/max/step slider (log-scaled where the node declares it), booleans a checkbox, and commands an **Execute** button. If the camera rejects a write, an error dialog is shown and the override list is not updated.
- **Toggling the Startup checkbox** — adds or removes the feature from the override list without writing to the camera. Useful for persisting a value that is already set on the camera.
- **Editor opened when the camera is not reachable** — shows the stored override list only (no live values). Any value typed goes straight into the list without a hardware round-trip.

**At workflow start**

1. The device is opened (using `SerialNumber`, `CameraModel`, or `DeviceIndex` — see priority above). If the device cannot be found, an error is thrown and no overrides are applied.
2. Each override is written to the camera in list order. Individual write failures are silently skipped (best-effort); the workflow starts regardless.

### EnumerateDevices

Emits a single `DeviceInfo[]` snapshot of every device visible on the transport layer, then completes. `DeviceInfo` exposes `GlobalIndex`, `Vendor`, `Model`, `SerialNumber`, `TLType`, `DisplayName`, `ProducerPath`, and GenTL identifiers — use it to discover cameras and their serials for pinning a `GenICamDevice`.

- `ProducerPath` — optional path to a specific `.cti` file; leave empty to enumerate across all producers on the system search path

### ListFeatureValues

Opens a device read-only, reads every accessible feature node, and emits a single `FeatureValue[]` snapshot, then completes. Use it for introspection — dumping the full feature set of a camera at design time.

- `ProducerPath` — optional path to a specific `.cti` file
- `DeviceIndex` — zero-based index of the camera in the enumerated device list

Each `FeatureValue` carries a `Name`, a `Type` (`Float`, `Integer`, `Boolean`, `String`, `Enumeration`), and the `Value` as `object`, with `AsFloat()` / `AsInt()` / `AsBool()` / `AsString()` accessors.

### CreateReadMessage / CreateWriteMessage

Build the request messages that drive a `GenICamDevice`. Both take a `FeatureName` and turn each upstream element into a message.

- **`CreateReadMessage`** — emits a `ReadRequest` for `FeatureName` on each upstream element, ignoring the element's value. Pair it with any timing source (e.g. a `Timer`) to poll a feature.
- **`CreateWriteMessage`** — emits a `WriteRequest` whose payload is the upstream value. Overloads accept `string`, `double`, `long`, `bool`, and `FeatureValue`; numeric values are formatted with invariant culture and booleans as `True`/`False`.

### FilterMessage

Filters a `GenICamMessage` stream by `FeatureName` and/or `MessageType`. Leave either property unset to pass everything for that criterion — e.g. set `MessageType = Frame` to pick only frames off a `GenICamDevice` that is also serving feature traffic, or set `FeatureName` to isolate one feature's responses.

### ParseFeature / ParseChunk

Strongly typed extractors built as expression operators — the output edge type is chosen at workflow compile time from the `FeatureType` property (`Float` → `double`, `Integer` → `long`, `Boolean` → `bool`, `String`/`Enumeration` → `string`). Messages or frames that do not match are silently skipped.

- **`ParseFeature`** — pulls the value of `FeatureName` out of `ReadResponse` messages. Place it after a `GenICamDevice` to convert read responses into a typed stream you can wire into arithmetic, logic, or visualizers.
- **`ParseChunk`** — pulls the value of a chunk field (e.g. `ChunkFrameID`, `ChunkExposureTime`) out of each `GenICamFrame`'s `ChunkData`. Requires `GenICamDevice.ChunkModeActive = true` and producer chunk support.

```
GenICamDevice ─► ParseFeature(ExposureTime, Float) ─► Multiply(2.0) ─► CreateWriteMessage(ExposureTime) ─► GenICamDevice
```

## Data types

- **`GenICamMessage`** — the unit flowing through `GenICamDevice`: `Type`, `FeatureName`, `Payload`, optional `Frame`.
- **`GenICamFrame`** — a captured frame: `Image` (OpenCV `IplImage`), `Width`/`Height`/`Depth`/`Channels`, buffer metadata (`Timestamp`, `TimestampNs`, `FrameId`, `IsIncomplete`), and `ChunkData` (a `name → value` dictionary, or `null` when chunk mode is off/unsupported).
- **`FeatureValue`** — a named feature and its typed value (see `ListFeatureValues`).
- **`DeviceInfo`** — a discovered device (see `EnumerateDevices`).

## License

MIT
