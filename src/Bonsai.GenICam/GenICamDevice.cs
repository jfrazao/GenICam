using System;
using System.ComponentModel;
using System.Drawing.Design;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.Design;
using Bonsai;
using Bonsai.GenICam.GenApi;
using Bonsai.GenICam.GenTL;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Single-owner GenICam device. Routes <see cref="GenICamMessage"/> feature requests
    /// (serialized on a dedicated <see cref="EventLoopScheduler"/> thread) and, when
    /// <see cref="AcquireFrames"/> is true, concurrently runs the acquisition loop and emits
    /// <see cref="GenICamMessageType.Frame"/> messages on the same output stream.
    /// Read requests produce <see cref="GenICamMessageType.ReadResponse"/> messages;
    /// write requests produce <see cref="GenICamMessageType.WriteAck"/> messages.
    /// </summary>
    [Description("Single-owner GenICam device: routes feature read/write messages and emits acquired frames on the same observable stream.")]
    [Editor("Bonsai.GenICam.GenICamDeviceEditor, Bonsai.GenICam", typeof(ComponentEditor))]
    public class GenICamDevice : Combinator<GenICamMessage, GenICamMessage>, IGenICamSource
    {
        private volatile NodeMap? _liveNodeMap;
        NodeMap? IGenICamSource.LiveNodeMap => _liveNodeMap;

        /// <summary>Gets or sets the path to a specific GenTL producer (.cti file). Leave empty to use the system search path.</summary>
        [Description("Path to a specific GenTL producer (.cti file). Leave empty to use the system search path.")]
        [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string? ProducerPath { get; set; }

        /// <summary>Gets or sets the zero-based index of the camera in the enumerated device list, or within the matching model group when <see cref="CameraModel"/> is set.</summary>
        [Description("Zero-based index of the camera in the enumerated device list, or within the matching model group when CameraModel is set.")]
        public int DeviceIndex { get; set; }

        /// <summary>Gets or sets the vendor+model string used to filter camera selection. Leave empty to select by <see cref="DeviceIndex"/> only.</summary>
        [Description("Optional: select camera by vendor+model string. Leave empty to select by DeviceIndex only.")]
        [Editor(typeof(CameraModelEditor), typeof(UITypeEditor))]
        public string? CameraModel { get; set; }

        /// <summary>Gets or sets the serial number used to identify the camera. When set, overrides <see cref="CameraModel"/> and <see cref="DeviceIndex"/>.</summary>
        [Description("Optional: select camera by serial number. When set, overrides CameraModel and DeviceIndex.")]
        [Editor(typeof(SerialNumberEditor), typeof(UITypeEditor))]
        public string? SerialNumber { get; set; }

        /// <summary>Gets or sets the number of acquisition buffers to allocate.</summary>
        [Description("Number of acquisition buffers to allocate.")]
        public int NumBuffers { get; set; } = 4;

        /// <summary>
        /// Gets or sets the timeout in milliseconds passed to <c>EventGetData</c> on each iteration
        /// of the acquisition loop. Normal teardown is driven by <c>EventKill</c>, which unblocks
        /// <c>EventGetData</c> immediately with <c>GC_ERR_ABORT</c> — this timeout is never reached
        /// under normal conditions. It exists as a fallback for GenTL producers that do not implement
        /// <c>EventKill</c> correctly: without it, a broken producer would cause the acquisition thread
        /// to block indefinitely on teardown. Set to roughly 2–3× your expected frame period.
        /// </summary>
        [Description("Timeout in milliseconds for each EventGetData call in the acquisition loop. " +
                     "Normal teardown uses EventKill and does not rely on this value; " +
                     "it is a safety net for producers that do not implement EventKill correctly.")]
        public uint FrameTimeoutMs { get; set; } = 5000;

        /// <summary>Gets or sets the camera feature values to apply before acquisition starts.</summary>
        [Description("Camera feature values to apply before acquisition starts.")]
        [TypeConverter(typeof(ExpandableObjectConverter))]
        [Editor(typeof(FeatureConfigurationEditor), typeof(UITypeEditor))]
        public FeatureConfiguration Features { get; set; } = new FeatureConfiguration();

        /// <summary>Gets or sets whether to run the acquisition loop and emit Frame messages. Set to false for feature-only access without streaming.</summary>
        [Description("Run the frame acquisition loop and emit Frame messages. Set to false for feature-only access.")]
        public bool AcquireFrames { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable GenICam chunk mode before starting acquisition.
        /// When true, the camera embeds per-frame metadata in each buffer and
        /// <see cref="GenICamFrame.ChunkData"/> is populated with the parsed values.
        /// Requires producer support for <c>DSGetBufferChunkData</c> (GenTL 1.5+);
        /// <c>ChunkData</c> will be <c>null</c> on every frame if not supported.
        /// </summary>
        [Description("Enable GenICam chunk mode — embeds per-frame metadata in each buffer. " +
                     "Requires producer support for DSGetBufferChunkData (GenTL 1.5+). " +
                     "ChunkData on each frame will be null if not supported.")]
        public bool ChunkModeActive { get; set; } = false;

        // Test-only seam (internal, off by default, not a workflow property): when set together
        // with ChunkModeActive, enables every chunk selector the camera exposes on the acquisition
        // connection before AcquisitionStart, so the buffer carries the full metadata set rather
        // than only what a UserSet happens to have enabled. Kept internal because auto-enabling all
        // chunks is opinionated and not universally supported — real workflows configure chunks via
        // a UserSet instead. Used by the unit-test harness to validate chunk decoding end-to-end.
        internal bool EnableAllChunks { get; set; } = false;

        /// <inheritdoc/>
        public override IObservable<GenICamMessage> Process(IObservable<GenICamMessage> source)
        {
            return Observable.Create<GenICamMessage>(observer =>
            {
                var ctx = OpenDevice();
                try
                {
                    var map = new NodeMap(ctx.Api, ctx.Port);
                    _liveNodeMap = map;

                    var cancel = new CancellationTokenSource();
                    var syncObs = Observer.Synchronize(observer);
                    var scheduler = new EventLoopScheduler();

                    // Apply startup overrides before acquisition starts and surface each result on the
                    // output stream: WriteAck for applied values, Error for rejected ones (e.g. a feature
                    // the connected camera does not expose). Uses the same TryWrite path as a runtime
                    // write, so startup failures are reported rather than silently swallowed.
                    foreach (var startupMsg in Features.Apply(map))
                        syncObs.OnNext(startupMsg);

                    // When not acquiring frames, propagate source completion so .Wait()/.ToArray() work.
                    // When acquiring frames, ignore source completion — the acquisition loop drives lifetime.
                    bool acquireFrames = AcquireFrames;
                    var featureSub = source
                        .ObserveOn(scheduler)
                        .Subscribe(
                            msg =>
                            {
                                try { syncObs.OnNext(Dispatch(msg, map)); }
                                catch (Exception ex) { syncObs.OnError(ex); }
                            },
                            ex => syncObs.OnError(ex),
                            () => { if (!acquireFrames) syncObs.OnCompleted(); });

                    AcqState? acqState = null;
                    Thread? acqThread = null;
                    if (AcquireFrames)
                    {
                        acqState = new AcqState
                        {
                            Ctx = ctx,
                            Map = map,
                            NumBuffers = NumBuffers,
                            FrameTimeoutMs = FrameTimeoutMs,
                            ChunkModeActive = ChunkModeActive,
                            EnableAllChunks = EnableAllChunks,
                            Cancel = cancel,
                            Observer = syncObs
                        };
                        acqThread = new Thread(obj =>
                        {
                            var s = (AcqState)obj!;
                            RunAcquisition(s);
                            s.CtxToClose?.Dispose();
                        });
                        acqThread.IsBackground = true;
                        acqThread.Name = "GenICamDevice";
                        acqThread.Start(acqState);
                    }

                    return System.Reactive.Disposables.Disposable.Create(() =>
                    {
                        cancel.Cancel();
                        acqState?.Stream?.InterruptWait();
                        featureSub.Dispose();
                        // Drain the scheduler before disposing it. ScheduledObserver.Run
                        // reschedules itself recursively via Schedule(). The race: if Run was
                        // mid-execution when featureSub.Dispose() fired, it can schedule one
                        // more Run AFTER the main-thread sentinel lands, so that Run executes
                        // after the sentinel but before Dispose() — hitting a disposed scheduler.
                        // Nesting two sentinels covers it: the inner fires only after that
                        // final stray Run has itself completed, guaranteeing an empty queue.
                        using (var drained = new ManualResetEventSlim(false))
                        {
                            scheduler.Schedule(() => scheduler.Schedule(() => drained.Set()));
                            drained.Wait(TimeSpan.FromSeconds(2));
                        }
                        scheduler.Dispose();
                        if (acqThread != null && Thread.CurrentThread == acqThread)
                        {
                            // Disposal is running on the acquisition thread (e.g. Take(N)
                            // triggered upstream dispose from within OnNext). Cannot Join
                            // ourselves — hand ctx to the thread; it closes after RunAcquisition's
                            // finally block finishes.
                            acqState!.CtxToClose = ctx;
                        }
                        else
                        {
                            acqThread?.Join(5000);
                            ctx.Dispose();
                        }
                        cancel.Dispose();
                        _liveNodeMap = null;
                    });
                }
                catch
                {
                    ctx.Dispose();
                    throw;
                }
            });
        }

        internal static GenICamMessage Dispatch(GenICamMessage msg, NodeMap map) => msg.Type switch
        {
            GenICamMessageType.WriteRequest => TryWrite(map, msg.FeatureName, msg.Payload ?? string.Empty),
            GenICamMessageType.ReadRequest  => TryRead(map, msg.FeatureName),
            _ => msg
        };

        // Single feature write/read, shared by the runtime message bus (Dispatch) and the startup-override
        // path (FeatureConfiguration.Apply). A rejected read/write (missing/NI feature, out-of-range or
        // read-only node, coercion failure) is a recoverable, per-feature condition: return an Error
        // message rather than throwing, so neither path faults the stream. Faulting would tear down the
        // whole device (capture + every other feature) and run the disposal/drain on the scheduler thread,
        // which is the ObjectDisposedException race in #13. Genuinely fatal device failures still surface
        // through the acquisition loop's OnError.
        internal static GenICamMessage TryWrite(NodeMap map, string name, string value)
        {
            try { map.Write(name, value); return GenICamMessage.Ack(name, value); }
            catch (Exception ex) { return GenICamMessage.Error(name, ex.Message); }
        }

        internal static GenICamMessage TryRead(NodeMap map, string name)
        {
            try { return GenICamMessage.Response(name, map.Read(name).ToPayloadString()); }
            catch (Exception ex) { return GenICamMessage.Error(name, ex.Message); }
        }

        [HandleProcessCorruptedStateExceptions]
        private static void RunAcquisition(AcqState s)
        {
            string step = "init";
            try
            {
                step = "open data stream";
                using var stream = s.Ctx.OpenDataStream();
                stream.SetFallbacks(
                    TryReadInt(s.Map, "Width"),
                    TryReadInt(s.Map, "Height"),
                    TryReadPixelFmt(s.Map));
                if (s.ChunkModeActive)
                {
                    try { s.Map.Write("ChunkModeActive", "true"); } catch { }
                    if (s.EnableAllChunks) EnableChunkSelectors(s.Map);
                    stream.EnableChunkMode(s.Map.ChunkIdToName, (name, bytes) => s.Map.TryReadChunk(name, bytes));
                }
                step = "start acquisition";
                stream.Start(s.NumBuffers);
                s.Stream = stream;
                TryExecuteCommand(s.Map, "AcquisitionStart");
                step = "capture loop";
                try
                {
                    while (!s.Cancel.IsCancellationRequested)
                    {
                        var frame = stream.WaitForFrame(s.FrameTimeoutMs);
                        if (frame != null)
                            s.Observer.OnNext(GenICamMessage.FromFrame(frame));
                    }
                }
                finally
                {
                    TryExecuteCommand(s.Map, "AcquisitionStop");
                    stream.Stop();
                }
            }
            catch (Exception ex) when (!s.Cancel.IsCancellationRequested)
            {
                s.Observer.OnError(new Exception($"GenICamDevice acquisition failed at [{step}]: {ex.Message}", ex));
            }
        }

        private static void TryExecuteCommand(NodeMap map, string name)
        {
            try { map.Write(name, ""); } catch { }
        }

        // Enables every chunk selector the camera exposes so their metadata is embedded in each
        // buffer. Must run on the acquisition connection before AcquisitionStart — many producers
        // only honor ChunkEnable for the connection that starts streaming, so without this the
        // buffer carries only the implicit image chunk. Uses the unfiltered entry list because some
        // producers report a selector as "unavailable" via its guards yet still accept the write;
        // selectors the device genuinely rejects are skipped.
        private static void EnableChunkSelectors(NodeMap map)
        {
            foreach (var sel in map.GetAllEnumEntries("ChunkSelector"))
            {
                try
                {
                    map.Write("ChunkSelector", sel);
                    map.Write("ChunkEnable", "true");
                }
                catch { /* selector not supported or not writable on this device */ }
            }
        }

        private static int TryReadInt(NodeMap map, string name)
        {
            try { return (int)(long)map.Read(name).Value; } catch { return 0; }
        }

        private static ulong TryReadPixelFmt(NodeMap map)
        {
            try
            {
                object v = map.Read("PixelFormat").Value;
                return PixelFormatNameToCode(v is string s ? s : v?.ToString() ?? string.Empty);
            }
            catch { return 0; }
        }

        private static ulong PixelFormatNameToCode(string name)
        {
            switch (name)
            {
                case "Mono8":    return 0x01080001;
                case "Mono10":   return 0x01100003;
                case "Mono12":   return 0x01100005;
                case "Mono16":   return 0x01100007;
                case "RGB8":     return 0x02180014;
                case "BGR8":     return 0x02180015;
                case "BayerGR8": return 0x01080008;
                case "BayerRG8": return 0x01080009;
                case "BayerGB8": return 0x0108000A;
                case "BayerBG8": return 0x0108000B;
                default:         return 0;
            }
        }

        private GenICamDeviceContext OpenDevice()
        {
            var path   = string.IsNullOrWhiteSpace(ProducerPath) ? null : ProducerPath;
            var serial = string.IsNullOrWhiteSpace(SerialNumber)  ? null : SerialNumber;
            var model  = string.IsNullOrWhiteSpace(CameraModel)   ? null : CameraModel;
            if (serial != null || model != null)
            {
                var (api, system, iface, device) = GenTLLoader.FindAndOpenDeviceAcrossProducers(
                    serial, model, DeviceIndex, path, DeviceAccessFlags.Control);
                return new GenICamDeviceContext(api, system, iface, device);
            }
            var (a, localIndex) = GenTLLoader.ResolveAndLoad(path, DeviceIndex);
            GenTLSystem? sys = null;
            try
            {
                sys = new GenTLSystem(a);
                var (_, _, ifc, dev) = sys.FindAndOpenDevice(localIndex);
                return new GenICamDeviceContext(a, sys, ifc, dev);
            }
            catch
            {
                sys?.Dispose();
                a.Dispose();
                throw;
            }
        }

        private sealed class AcqState
        {
            internal GenICamDeviceContext Ctx = null!;
            internal NodeMap Map = null!;
            internal int NumBuffers;
            internal uint FrameTimeoutMs;
            internal bool ChunkModeActive;
            internal bool EnableAllChunks;
            internal CancellationTokenSource Cancel = null!;
            internal IObserver<GenICamMessage> Observer = null!;
            internal volatile GenTLDataStream? Stream;
            internal volatile GenICamDeviceContext? CtxToClose;
        }
    }

    internal sealed class GenICamDeviceContext : IDisposable
    {
        internal readonly GenTLApi Api;
        internal readonly IntPtr Port;
        private readonly GenTLSystem _system;
        private readonly GenTLInterface _iface;
        private readonly GenTLDevice _device;

        internal GenICamDeviceContext(GenTLApi api, GenTLSystem system, GenTLInterface iface, GenTLDevice device)
        {
            Api = api;
            _system = system;
            _iface = iface;
            _device = device;
            Port = device.GetPort();
        }

        internal GenTLDataStream OpenDataStream() => _device.OpenDataStream();

        public void Dispose()
        {
            _device.Dispose();
            _iface.Dispose();
            _system.Dispose();
            Api.Dispose();
        }
    }

    internal class CameraModelEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) =>
            UITypeEditorEditStyle.DropDown;

        public override object? EditValue(ITypeDescriptorContext context, IServiceProvider provider, object? value)
        {
            var svc = provider?.GetService(typeof(IWindowsFormsEditorService)) as IWindowsFormsEditorService;
            if (svc == null || !(context?.Instance is IGenICamSource source)) return value;

            var lb = new ListBox { SelectionMode = SelectionMode.One, Height = 120 };
            lb.Items.Add("(none — select by DeviceIndex only)");

            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = string.IsNullOrWhiteSpace(source.ProducerPath) ? null : source.ProducerPath;
                foreach (var info in GenTLLoader.EnumerateAllDeviceInfos(path))
                {
                    string combined = (info.Vendor + " " + info.Model).Trim();
                    if (!string.IsNullOrEmpty(combined) && seen.Add(combined))
                        lb.Items.Add(combined);
                }
            }
            catch { }

            if (value is string cur && !string.IsNullOrEmpty(cur))
            {
                int idx = lb.Items.IndexOf(cur);
                if (idx >= 0) lb.SelectedIndex = idx;
            }

            lb.Click += (s, e) => svc.CloseDropDown();
            svc.DropDownControl(lb);

            if (lb.SelectedIndex <= 0) return null;
            return lb.SelectedItem as string ?? value;
        }
    }

    internal class SerialNumberEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) =>
            UITypeEditorEditStyle.DropDown;

        public override object? EditValue(ITypeDescriptorContext context, IServiceProvider provider, object? value)
        {
            var svc = provider?.GetService(typeof(IWindowsFormsEditorService)) as IWindowsFormsEditorService;
            if (svc == null || !(context?.Instance is IGenICamSource source)) return value;

            var lb = new ListBox { SelectionMode = SelectionMode.One, Height = 120 };
            lb.Items.Add("(none — match by model or index)");

            try
            {
                var path = string.IsNullOrWhiteSpace(source.ProducerPath) ? null : source.ProducerPath;
                foreach (var info in GenTLLoader.EnumerateAllDeviceInfos(path))
                {
                    if (!string.IsNullOrEmpty(info.SerialNumber))
                        lb.Items.Add(info.SerialNumber);
                }
            }
            catch { }

            if (value is string cur && !string.IsNullOrEmpty(cur))
            {
                int idx = lb.Items.IndexOf(cur);
                if (idx >= 0) lb.SelectedIndex = idx;
            }

            lb.Click += (s, e) => svc.CloseDropDown();
            svc.DropDownControl(lb);

            if (lb.SelectedIndex <= 0) return null;
            return lb.SelectedItem as string ?? value;
        }
    }
}
