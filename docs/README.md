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
| `GetFeatureNode` | `Source<FeatureValue>` | Reads a named feature; value is `object` at runtime |
| `GetIntFeature` | `Source<long>` | Reads a GenICam Integer feature as `long` |
| `GetFloatFeature` | `Source<double>` | Reads a GenICam Float feature as `double` |
| `GetBoolFeature` | `Source<bool>` | Reads a GenICam Boolean feature as `bool` |
| `GetStringFeature` | `Source<string>` | Reads a GenICam String or Enumeration feature as `string` |
| `SetFeatureNode` | `Combinator` | Writes a named feature from a string `Value` property on each upstream element |
| `SetIntFeature` | `Combinator<long, long>` | Writes a GenICam Integer feature from an upstream `long` |
| `SetFloatFeature` | `Combinator<double, double>` | Writes a GenICam Float feature from an upstream `double` |
| `SetBoolFeature` | `Combinator<bool, bool>` | Writes a GenICam Boolean feature from an upstream `bool` |
| `SetStringFeature` | `Combinator<string, string>` | Writes a GenICam String or Enumeration feature from an upstream `string` |
| `ListFeatureValues` | `Source<FeatureValue[]>` | Reads all readable features from a device |

### GenICamCapture

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

### GetFeatureNode / SetFeatureNode / ListFeatureValues

All three operators share the same camera-selection properties as `GenICamCapture`:

- `ProducerPath` — optional path to a specific `.cti` file
- `DeviceIndex` — zero-based index; when `CameraModel` is set, counts only within the matching model group
- `CameraModel` — optional vendor+model filter; click the dropdown to pick from detected cameras
- `SerialNumber` — optional serial number; overrides `CameraModel` and `DeviceIndex` when set

Camera selection follows the same priority as `GenICamCapture` (SerialNumber → CameraModel → DeviceIndex).

`GetFeatureNode` and `SetFeatureNode` also have:

- `FeatureCategory` — optional category filter; click the dropdown to pick a GenICam category (e.g. `AcquisitionControl`, `ImageFormatControl`). Filters the `FeatureName` dropdown — leave blank to browse all features.
- `FeatureName` — GenICam XML feature name; click the dropdown to pick from the features in the selected category (or all features if no category is set). Populated by connecting to the camera on demand.
- `Value` *(SetFeatureNode only)* — value as a string, parsed to the node's type at runtime
- `PeriodMs` *(GetFeatureNode only)* — interval between reads in ms; `0` emits a single value and completes (default `1000`)

#### Shared device connection

When any feature operator targets the same camera as a running `GenICamCapture` (matched by serial, model+index, or producer+index), it reuses `GenICamCapture`'s open `NodeMap` instead of opening a competing GenTL connection. This avoids the `TLOpen` contention that causes some producers to report zero devices when two operators try to connect simultaneously.

### Typed feature operators

`GetIntFeature`, `GetFloatFeature`, `GetBoolFeature`, and `GetStringFeature` are typed variants of `GetFeatureNode` that emit native .NET types directly. `SetIntFeature`, `SetFloatFeature`, `SetBoolFeature`, and `SetStringFeature` are typed variants of `SetFeatureNode` that accept typed upstream observables instead of a string `Value` property.

| Node kind | Get operator | Set operator |
|---|---|---|
| Integer | `GetIntFeature` → `long` | `SetIntFeature` ← `long` |
| Float | `GetFloatFeature` → `double` | `SetFloatFeature` ← `double` |
| Boolean | `GetBoolFeature` → `bool` | `SetBoolFeature` ← `bool` |
| String / Enumeration | `GetStringFeature` → `string` | `SetStringFeature` ← `string` |

Use the typed variants when you want to wire feature values directly into arithmetic, logic, or other typed operators without an intermediate cast step:

```
GetFloatFeature(FeatureName="ExposureTime") → Multiply(2.0) → SetFloatFeature(FeatureName="ExposureTime")
```

Use `GetFeatureNode` / `SetFeatureNode` when the feature type is unknown at design time, or for introspection workflows that handle multiple features of different types.

All typed operators share the same camera-selection properties and `FeatureCategory`/`FeatureName` dropdowns as `GetFeatureNode` / `SetFeatureNode`, and reuse `GenICamCapture`'s open `NodeMap` via `NodeMapRegistry` when targeting the same camera. `FeatureName` is read on every sample — changing it in the property grid while the workflow is running takes effect immediately on the next sample.

## License

MIT
