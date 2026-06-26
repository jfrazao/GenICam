using System;
using System.Collections.Generic;
using System.IO;

namespace Bonsai.GenICam.GenTL
{
    internal static class GenTLLoader
    {
        internal static readonly object ScanLock = new object();

        // Returns all .cti files found on GENICAM_GENTL64_PATH / GENICAM_GENTL32_PATH.
        // Reads Machine + User scopes and merges them to avoid missing entries that aren't
        // in the process-inherited value (e.g. set after Bonsai was launched).
        internal static IEnumerable<string> FindProducers()
        {
            string envVar = IntPtr.Size == 8 ? "GENICAM_GENTL64_PATH" : "GENICAM_GENTL32_PATH";

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new List<string>();
            foreach (var scope in new[] {
                EnvironmentVariableTarget.Process,
                EnvironmentVariableTarget.Machine,
                EnvironmentVariableTarget.User })
            {
                string val = Environment.GetEnvironmentVariable(envVar, scope);
                if (string.IsNullOrWhiteSpace(val)) continue;
                foreach (string entry in val.Split(';'))
                {
                    string d = entry.Trim().Trim('"');
                    if (d.Length > 0 && seen.Add(d))
                        dirs.Add(d);
                }
            }

            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string cti in Directory.GetFiles(dir, "*.cti"))
                    yield return cti;
            }
        }

        // Load and initialize a GenTL producer. If ctiPath is null, uses the first producer
        // found on the system search path.
        internal static GenTLApi Load(string? ctiPath = null)
        {
            if (ctiPath != null)
                return new GenTLApi(ctiPath);

            string envVar = IntPtr.Size == 8 ? "GENICAM_GENTL64_PATH" : "GENICAM_GENTL32_PATH";
            foreach (string path in FindProducers())
                return new GenTLApi(path);

            throw new InvalidOperationException(
                $"No GenTL producers found. Set {envVar} or specify a ProducerPath.");
        }

        // Enumerate devices from all producers (or just the specified one).
        // Same logic as EnumerateDevices operator — disposes api/system internally, caller gets DeviceInfo only.
        internal static DeviceInfo[] EnumerateAllDeviceInfos(string? explicitProducerPath)
        {
            var results = new List<DeviceInfo>();
            int globalIndex = 0;

            IEnumerable<string> paths = explicitProducerPath != null
                ? (IEnumerable<string>)new[] { explicitProducerPath }
                : FindProducers();

            lock (ScanLock)
            foreach (string ctiPath in paths)
            {
                try
                {
                    using (var api = new GenTLApi(ctiPath))
                    using (var system = new GenTLSystem(api))
                    {
                        foreach (string ifaceId in system.GetInterfaceIDs())
                        {
                            using (var iface = system.OpenInterface(ifaceId))
                            {
                                foreach (string devId in iface.GetDeviceIDs())
                                {
                                    var info = iface.GetDeviceInfo(devId);
                                    info.GlobalIndex  = globalIndex++;
                                    info.InterfaceID  = ifaceId;
                                    info.ProducerPath = api.ProducerPath;
                                    results.Add(info);
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            return results.ToArray();
        }

        // Find and open a device by serial or model across all producers (or just the specified one).
        // Caller owns all four returned objects and must dispose them.
        internal static (GenTLApi api, GenTLSystem system, GenTLInterface iface, GenTLDevice device, string ifaceId, string devId)
            FindAndOpenDeviceAcrossProducers(
                string? serialNumber, string? cameraModel, int modelIndex,
                string? explicitProducerPath = null,
                DeviceAccessFlags flags = DeviceAccessFlags.Control)
        {
            IEnumerable<string> paths = explicitProducerPath != null
                ? (IEnumerable<string>)new[] { explicitProducerPath }
                : FindProducers();

            foreach (string ctiPath in paths)
            {
                GenTLApi? api = null;
                GenTLSystem? system = null;
                try
                {
                    api = new GenTLApi(ctiPath);
                    system = new GenTLSystem(api);
                    var r = serialNumber != null
                        ? system.FindAndOpenDeviceBySerial(serialNumber, flags)
                        : system.FindAndOpenDeviceByModel(cameraModel!, modelIndex, flags);
                    return (api, system, r.iface, r.device, r.ifaceId, r.devId);
                }
                catch
                {
                    system?.Dispose();
                    api?.Dispose();
                }
            }

            string desc = serialNumber != null
                ? $"serial number '{serialNumber}'"
                : $"model '{cameraModel}' at index {modelIndex}";
            throw new InvalidOperationException($"No camera with {desc} found in any GenTL producer.");
        }

        // Loads the correct producer for the given device index and returns it already initialized,
        // avoiding a double-load race. When explicitProducerPath is set uses only that producer.
        // Caller owns the returned GenTLApi and must dispose it.
        // Serialized via _scanLock so concurrent workflow starts don't race on GCInitLib/GCCloseLib.
        internal static (GenTLApi api, int localIndex) ResolveAndLoad(string? explicitProducerPath, int globalDeviceIndex)
        {
            if (explicitProducerPath != null)
                return (Load(explicitProducerPath), globalDeviceIndex);

            lock (ScanLock)
            {
                int offset = 0;
                foreach (string ctiPath in FindProducers())
                {
                    GenTLApi? api = null;
                    try
                    {
                        api = new GenTLApi(ctiPath);
                        int count;
                        using (var system = new GenTLSystem(api))
                            count = system.CountDevices();

                        if (offset + count > globalDeviceIndex)
                            return (api, globalDeviceIndex - offset);

                        offset += count;
                        api.Dispose();
                        api = null;
                    }
                    catch
                    {
                        api?.Dispose();
                    }
                }

                string envVar = IntPtr.Size == 8 ? "GENICAM_GENTL64_PATH" : "GENICAM_GENTL32_PATH";
                throw new InvalidOperationException(
                    $"No GenTL device found at index {globalDeviceIndex}. Check {envVar} and camera connections.");
            }
        }
    }
}
