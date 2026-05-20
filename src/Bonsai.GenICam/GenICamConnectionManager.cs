using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Bonsai.GenICam.GenApi;
using System.Reactive.Disposables;

namespace Bonsai.GenICam
{
    // Shared GenTL device connection, keyed by the user-assigned Name on GenICamCapture.
    // Mirrors the Bonsai SerialPortManager pattern: whoever calls ReserveConnection first
    // opens the device; subsequent callers share the same NodeMap via reference counting.
    // The underlying device is closed only when the last ref is released.
    internal sealed class SharedNodeMap : IDisposable
    {
        internal readonly NodeMap NodeMap;
        private readonly IDisposable _lifetime;

        internal SharedNodeMap(NodeMap map, IDisposable lifetime)
        {
            NodeMap = map;
            _lifetime = lifetime;
        }

        public void Dispose() => _lifetime.Dispose();
    }

    internal static class GenICamConnectionManager
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _declaredNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Called by GenICamCapture when its Name property is set at design or runtime.
        internal static void RegisterName(string name)   { lock (_lock) { _declaredNames.Add(name); } }
        internal static void UnregisterName(string name) { lock (_lock) { _declaredNames.Remove(name); } }
        internal static string[] GetDeclaredNames()      { lock (_lock) { return _declaredNames.ToArray(); } }

        private sealed class Entry
        {
            internal readonly GenICamDeviceContext Ctx;
            internal readonly NodeMap NodeMap;
            private int _refCount;

            internal Entry(GenICamDeviceContext ctx, NodeMap nodeMap)
            {
                Ctx = ctx;
                NodeMap = nodeMap;
                _refCount = 1;
            }

            internal void AddRef() => _refCount++;
            internal bool Release() => --_refCount == 0;
        }

        // Called by GenICamCapture once the device is open and the NodeMap is built.
        // Returns a disposable that, when disposed, releases the capture's ref.
        // If all refs are gone the device is closed.
        internal static IDisposable Publish(string name, GenICamDeviceContext ctx, NodeMap nodeMap)
        {
            var entry = new Entry(ctx, nodeMap);
            lock (_lock)
            {
                _entries[name] = entry;
                Monitor.PulseAll(_lock);
            }
            return Disposable.Create(() =>
            {
                bool last;
                lock (_lock)
                {
                    last = entry.Release();
                    if (last) _entries.Remove(name);
                    Monitor.PulseAll(_lock);
                }
                if (last) ctx.Dispose();
            });
        }

        // Called by feature operators that set Connection = "<name>".
        // Blocks until GenICamCapture publishes the connection or the timeout elapses.
        // Returns null on timeout; caller should surface an appropriate error.
        internal static SharedNodeMap? Acquire(string name, int timeoutMs = 10000)
        {
            lock (_lock)
            {
                var sw = Stopwatch.StartNew();
                while (!_entries.TryGetValue(name, out _))
                {
                    long remaining = timeoutMs - sw.ElapsedMilliseconds;
                    if (remaining <= 0) return null;
                    Monitor.Wait(_lock, (int)Math.Min(remaining, 200));
                }

                var entry = _entries[name];
                entry.AddRef();
                var captured = entry;
                var lifetime = Disposable.Create(() =>
                {
                    bool last;
                    lock (_lock)
                    {
                        last = captured.Release();
                        if (last) _entries.Remove(name);
                        Monitor.PulseAll(_lock);
                    }
                    if (last) captured.Ctx.Dispose();
                });
                return new SharedNodeMap(entry.NodeMap, lifetime);
            }
        }
    }
}
