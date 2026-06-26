using System;

namespace Bonsai.GenICam.GenTL
{
    /// <summary>
    /// Base class for the GenTL module wrappers (system, interface, device, data stream). Holds the
    /// producer (<see cref="_api"/>) and the module handle (<see cref="_handle"/>), and implements the
    /// shared "close once, then null the handle" disposal contract. Subclasses supply only their close
    /// call via <see cref="CloseHandle"/>, and may run extra teardown via <see cref="OnDisposing"/>.
    /// </summary>
    internal abstract class GenTLHandle : IDisposable
    {
        protected readonly GenTLApi _api;
        protected IntPtr _handle;

        protected GenTLHandle(GenTLApi api) => _api = api;

        /// <summary>Releases the native module handle (e.g. <c>TLClose</c>/<c>IFClose</c>/<c>DevClose</c>/<c>DSClose</c>).</summary>
        protected abstract void CloseHandle();

        /// <summary>Optional teardown run before the handle is closed (e.g. stopping acquisition).</summary>
        protected virtual void OnDisposing() { }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                OnDisposing();
                CloseHandle();
                _handle = IntPtr.Zero;
            }
        }
    }
}
