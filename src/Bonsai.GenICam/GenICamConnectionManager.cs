using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Bonsai.GenICam.GenApi;

namespace Bonsai.GenICam
{
    internal static class GenICamConnectionManager
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, BehaviorSubject<NodeMap?>> _subjects =
            new Dictionary<string, BehaviorSubject<NodeMap?>>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _declaredNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static void RegisterName(string name)
        {
            lock (_lock)
            {
                _declaredNames.Add(name);
                if (!_subjects.ContainsKey(name))
                    _subjects[name] = new BehaviorSubject<NodeMap?>(null);
            }
        }

        internal static void UnregisterName(string name)
        {
            lock (_lock) { _declaredNames.Remove(name); }
        }

        internal static string[] GetDeclaredNames()
        {
            lock (_lock) { return _declaredNames.ToArray(); }
        }

        // Called by GenICamCapture once the NodeMap is ready.
        // Returns a disposable that signals disconnection when disposed.
        internal static IDisposable Publish(string name, NodeMap nodeMap)
        {
            var subject = GetOrCreate(name);
            subject.OnNext(nodeMap);
            return Disposable.Create(() => subject.OnNext(null));
        }

        // Returns an observable that emits the NodeMap as soon as it is available,
        // then completes. Errors with InvalidOperationException after 10 seconds.
        internal static IObservable<NodeMap> Acquire(string name)
        {
            return Observable.Defer(() =>
                GetOrCreate(name)
                    .Where(m => m != null)
                    .Select(m => m!)
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(10))
                    .Catch<NodeMap, TimeoutException>(
                        _ => Observable.Throw<NodeMap>(new InvalidOperationException(
                            $"GenICamCapture named '{name}' did not publish a connection within the timeout."))));
        }

        private static BehaviorSubject<NodeMap?> GetOrCreate(string name)
        {
            lock (_lock)
            {
                if (!_subjects.TryGetValue(name, out var subject))
                {
                    subject = new BehaviorSubject<NodeMap?>(null);
                    _subjects[name] = subject;
                }
                return subject;
            }
        }
    }
}
