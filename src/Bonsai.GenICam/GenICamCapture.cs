using System;
using System.ComponentModel;
using System.Drawing.Design;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using Bonsai;
using Bonsai.GenICam.GenApi;
using Bonsai.GenICam.GenTL;
using OpenCV.Net;

namespace Bonsai.GenICam
{
    [Description("Acquires a sequence of images from a GenICam GenTL camera.")]
    public class GenICamCapture : Source<IplImage>, IGenICamSource
    {
        [Description("Path to a specific GenTL producer (.cti file). Leave empty to use the system search path.")]
        [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string? ProducerPath { get; set; }

        [Description("Zero-based index of the camera in the enumerated device list.")]
        public int DeviceIndex { get; set; }

        [Description("Number of acquisition buffers to allocate.")]
        public int NumBuffers { get; set; } = 4;

        [Description("Timeout in milliseconds to wait for each frame.")]
        public uint FrameTimeoutMs { get; set; } = 5000;

        [Description("Camera feature values to apply before acquisition starts.")]
        [TypeConverter(typeof(ExpandableObjectConverter))]
        [Editor(typeof(FeatureConfigurationEditor), typeof(UITypeEditor))]
        public FeatureConfiguration Features { get; set; } = new FeatureConfiguration();

        public override IObservable<IplImage> Generate()
        {
            return Observable.Create<IplImage>(observer =>
            {
                var state = new CaptureState
                {
                    Observer = observer,
                    ProducerPath = string.IsNullOrWhiteSpace(ProducerPath) ? null : ProducerPath,
                    DeviceIndex = DeviceIndex,
                    NumBuffers = NumBuffers,
                    FrameTimeoutMs = FrameTimeoutMs,
                    Features = Features,
                    Cancel = new CancellationTokenSource()
                };

                var thread = new Thread(RunCapture);
                thread.IsBackground = true;
                thread.Name = "GenICamCapture";
                thread.Start(state);

                return Disposable.Create(() =>
                {
                    state.Cancel.Cancel();
                    state.Stream?.InterruptWait(); // unblocks EventGetData immediately
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
                step = "resolve producer";
                var (api, localIndex) = GenTLLoader.ResolveAndLoad(s.ProducerPath, s.DeviceIndex);
                using (api)
                using (var system = new GenTLSystem(api))
                {
                    step = "find and open device";
                    var (_, _, iface, device) = system.FindAndOpenDevice(localIndex);
                    using (iface)
                    using (device)
                    {
                        step = "fetch device XML / build NodeMap";
                        var nodeMap = new NodeMap(api, device.GetPort());
                        s.Features.Apply(nodeMap);
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
                                TryExecuteCommand(nodeMap, "AcquisitionStop");
                                stream.Stop();
                            }
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
            public int NumBuffers;
            public uint FrameTimeoutMs;
            public FeatureConfiguration Features = null!;
            public CancellationTokenSource Cancel = null!;
            public volatile GenTLDataStream? Stream; // set by capture thread, read by dispose
        }
    }
}
