using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Design;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.Design;
using Bonsai;
using Bonsai.GenICam.GenApi;
using Bonsai.GenICam.GenTL;
using OpenCV.Net;

namespace Bonsai.GenICam
{
    [Description("Acquires a sequence of images from a GenICam GenTL camera.")]
    [Editor("Bonsai.GenICam.GenICamCaptureEditor, Bonsai.GenICam", typeof(ComponentEditor))]
    public class GenICamCapture : Source<IplImage>, IGenICamSource, INotifyPropertyChanged
    {
        private string? _producerPath;
        private int _deviceIndex;
        private string? _cameraModel;
        private string? _serialNumber;
        private volatile NodeMap? _liveNodeMap;

        NodeMap? IGenICamSource.LiveNodeMap => _liveNodeMap;

        public event PropertyChangedEventHandler? PropertyChanged;

        [Description("Path to a specific GenTL producer (.cti file). Leave empty to use the system search path.")]
        [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string? ProducerPath
        {
            get => _producerPath;
            set { if (_producerPath == value) return; _producerPath = value; NotifyIdentityChanged(); }
        }

        [Description("Zero-based index of the camera in the enumerated device list, or within the matching model group when CameraModel is set.")]
        public int DeviceIndex
        {
            get => _deviceIndex;
            set
            {
                if (_deviceIndex == value) return;
                _deviceIndex = value;
                // No model filter means global index — any change may land on a different model.
                if (_cameraModel == null && Features.Overrides.Count > 0)
                    Features = new FeatureConfiguration();
                NotifyIdentityChanged();
            }
        }

        [Description("Optional: select camera by vendor+model string (e.g. 'Basler Blackfly S BFS-U3-16S2M'). Leave empty to select by DeviceIndex only.")]
        [Editor(typeof(CameraModelEditor), typeof(UITypeEditor))]
        public string? CameraModel
        {
            get => _cameraModel;
            set
            {
                var v = string.IsNullOrWhiteSpace(value) ? null : value;
                if (_cameraModel == v) return;
                _cameraModel = v;
                Features = new FeatureConfiguration();
                NotifyIdentityChanged();
            }
        }

        [Description("Optional: select camera by serial number. When set, overrides CameraModel and DeviceIndex for device lookup; a mismatch causes an error at startup.")]
        [Editor(typeof(SerialNumberEditor), typeof(UITypeEditor))]
        public string? SerialNumber
        {
            get => _serialNumber;
            set
            {
                var v = string.IsNullOrWhiteSpace(value) ? null : value;
                if (_serialNumber == v) return;
                _serialNumber = v;
                if (_cameraModel == null && Features.Overrides.Count > 0)
                    Features = new FeatureConfiguration();
                NotifyIdentityChanged();
            }
        }

        [Description("Number of acquisition buffers to allocate.")]
        public int NumBuffers { get; set; } = 4;

        [Description("Timeout in milliseconds to wait for each frame.")]
        public uint FrameTimeoutMs { get; set; } = 5000;

        [Description("Camera feature values to apply before acquisition starts.")]
        [TypeConverter(typeof(ExpandableObjectConverter))]
        [Editor(typeof(FeatureConfigurationEditor), typeof(UITypeEditor))]
        public FeatureConfiguration Features { get; set; } = new FeatureConfiguration();

        private void NotifyIdentityChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Features)));
        }

        public override IObservable<IplImage> Generate()
        {
            return Observable.Create<IplImage>(observer =>
            {
                var state = new CaptureState
                {
                    Observer = observer,
                    ProducerPath = string.IsNullOrWhiteSpace(ProducerPath) ? null : ProducerPath,
                    DeviceIndex = DeviceIndex,
                    CameraModel = string.IsNullOrWhiteSpace(CameraModel) ? null : CameraModel,
                    SerialNumber = string.IsNullOrWhiteSpace(SerialNumber) ? null : SerialNumber,
                    NumBuffers = NumBuffers,
                    FrameTimeoutMs = FrameTimeoutMs,
                    Features = Features,
                    Cancel = new CancellationTokenSource(),
                    SetLiveNodeMap = map => _liveNodeMap = map
                };

                var thread = new Thread(RunCapture);
                thread.IsBackground = true;
                thread.Name = "GenICamCapture";
                thread.Start(state);

                return Disposable.Create(() =>
                {
                    state.Cancel.Cancel();
                    state.Stream?.InterruptWait();
                    thread.Join(5000);
                    state.Cancel.Dispose();
                });
            });
        }

        [HandleProcessCorruptedStateExceptions]
        private static void RunCapture(object obj)
        {
            var s = (CaptureState)obj;
            string step = "init";
            try
            {
                step = "open device";
                var (api, system, iface, device) = OpenCamera(s);
                using (api)
                using (system)
                using (iface)
                using (device)
                {
                    step = "fetch device XML / build NodeMap";
                    var nodeMap = new NodeMap(api, device.GetPort());
                    s.Features.Apply(nodeMap);
                    s.SetLiveNodeMap(nodeMap);
                    step = "open data stream";
                    using (var stream = device.OpenDataStream())
                    {
                        stream.SetFallbacks(
                            TryReadInt(nodeMap, "Width"),
                            TryReadInt(nodeMap, "Height"),
                            TryReadPixelFmt(nodeMap));
                        step = "start acquisition";
                        stream.Start(s.NumBuffers);
                        s.Stream = stream;
                        TryExecuteCommand(nodeMap, "AcquisitionStart");
                        step = "capture loop";
                        try
                        {
                            while (!s.Cancel.IsCancellationRequested)
                            {
                                var frame = stream.WaitForFrame(s.FrameTimeoutMs);
                                if (frame != null)
                                    s.Observer.OnNext(frame);
                            }
                        }
                        finally
                        {
                            s.SetLiveNodeMap(null);
                            TryExecuteCommand(nodeMap, "AcquisitionStop");
                            stream.Stop();
                        }
                    }
                }
                s.Observer.OnCompleted();
            }
            catch (Exception ex) when (!s.Cancel.IsCancellationRequested)
            {
                s.Observer.OnError(new Exception($"GenICamCapture failed at [{step}]: {ex.Message}", ex));
            }
        }

        private static (GenTLApi api, GenTLSystem system, GenTLInterface iface, GenTLDevice device)
            OpenCamera(CaptureState s)
        {
            if (s.SerialNumber != null || s.CameraModel != null)
            {
                // Identity-based selection: search the explicit producer, or all producers if none set.
                return GenTLLoader.FindAndOpenDeviceAcrossProducers(
                    s.SerialNumber, s.CameraModel, s.DeviceIndex, s.ProducerPath);
            }
            else
            {
                // Index-based selection: ResolveAndLoad finds the right producer for the global index.
                var (api, localIndex) = GenTLLoader.ResolveAndLoad(s.ProducerPath, s.DeviceIndex);
                GenTLSystem? system = null;
                try
                {
                    system = new GenTLSystem(api);
                    var r = system.FindAndOpenDevice(localIndex);
                    return (api, system, r.iface, r.device);
                }
                catch
                {
                    system?.Dispose();
                    api.Dispose();
                    throw;
                }
            }
        }

        [HandleProcessCorruptedStateExceptions]
        private static void TryExecuteCommand(NodeMap nodeMap, string commandName)
        {
            try { nodeMap.Write(commandName, ""); }
            catch { }
        }

        private static int TryReadInt(NodeMap nodeMap, string name)
        {
            try { return (int)(long)nodeMap.Read(name).Value; }
            catch { return 0; }
        }

        private static ulong TryReadPixelFmt(NodeMap nodeMap)
        {
            try
            {
                object v = nodeMap.Read("PixelFormat").Value;
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

        private sealed class CaptureState
        {
            public IObserver<IplImage> Observer = null!;
            public string? ProducerPath;
            public int DeviceIndex;
            public string? CameraModel;
            public string? SerialNumber;
            public int NumBuffers;
            public uint FrameTimeoutMs;
            public FeatureConfiguration Features = null!;
            public CancellationTokenSource Cancel = null!;
            public volatile GenTLDataStream? Stream;
            public Action<NodeMap?> SetLiveNodeMap = null!;
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

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
