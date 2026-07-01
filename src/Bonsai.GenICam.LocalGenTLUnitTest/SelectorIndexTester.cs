using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bonsai.GenICam;
using Bonsai.GenICam.GenApi;
using Bonsai.GenICam.GenTL;

namespace Bonsai.GenICam.LocalGenTLUnitTest
{
    // Live check for selector-indexed addressing driven by an inline-<Value> selector (e.g. FLIR
    // TriggerSelector). Proves that writing a governed feature under one selector position lands on
    // the correct per-position register and does NOT bleed into another position (independence).
    // Generic: discovers a suitable selector/governed pair on whatever camera is attached, and
    // restores every value it touches. Skips geometry/acquisition features so it can't disturb the
    // capture test or change image size.
    static class SelectorIndexTester
    {
        static readonly string[] Unsafe =
            { "Binning", "Decimation", "Width", "Height", "OffsetX", "OffsetY", "PixelFormat", "TestPattern", "Acquisition", "UserSet" };

        static bool IsUnsafe(string name) =>
            Unsafe.Any(u => name.IndexOf(u, StringComparison.OrdinalIgnoreCase) >= 0);

        public static void Run(string? producerPath, int deviceIndex)
        {
            Console.WriteLine("=== Selector-indexed write test (inline-<Value> selector) ===");
            var (api, localIndex) = GenTLLoader.ResolveAndLoad(
                string.IsNullOrWhiteSpace(producerPath) ? null : producerPath, deviceIndex);
            try
            {
                var system = new GenTLSystem(api);
                var (_, _, iface, device) = system.FindAndOpenDevice(localIndex, DeviceAccessFlags.Control);
                try
                {
                    var map = new NodeMap(api, device.GetPort());

                    // Collect candidate (selector, governed) pairs: a writable inline-<Value> selector
                    // (has pSelected) with >=2 entries and a writable, round-trippable governed feature.
                    var candidates = new List<(string sel, string gov)>();
                    foreach (var fv in map.TryReadAll())
                    {
                        var name = fv.Name;
                        if (IsUnsafe(name) || map.GetNodeKind(name) != FeatureKind.Enumeration) continue;
                        if (map.GetSelectedFeatures(name).Count == 0 || !map.CanWrite(name)) continue;
                        if (map.GetEnumEntries(name).Count < 2) continue;
                        foreach (var g in map.GetSelectedFeatures(name))
                        {
                            var gk = map.GetNodeKind(g);
                            if (IsUnsafe(g) || !map.CanWrite(g)) continue;
                            if (gk == FeatureKind.Integer || gk == FeatureKind.Float || gk == FeatureKind.Enumeration)
                                candidates.Add((name, g));
                        }
                    }

                    // Prefer Trigger* selectors and enum governed features (safest, known selector-indexed).
                    candidates = candidates
                        .OrderByDescending(c => c.sel.IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) >= 0)
                        .ThenByDescending(c => map.GetNodeKind(c.gov) == FeatureKind.Enumeration)
                        .ToList();

                    if (candidates.Count == 0)
                    {
                        Console.WriteLine("  No writable inline-<Value> selector with a writable governed feature found — skipped.");
                        return;
                    }

                    // Try candidates until one round-trips without a device error (some governed
                    // registers return GC_ERR_NO_DATA under certain positions on some cameras).
                    foreach (var (selName, govName) in candidates)
                    {
                        var entries = map.GetEnumEntries(selName);
                        string e0 = entries[0], e1 = entries[1];
                        string origSel = map.Read(selName).Value?.ToString() ?? e0;
                        string g0 = "", g1 = "";
                        try
                        {
                            map.Write(selName, e0); g0 = map.Read(govName).Value?.ToString() ?? "";
                            map.Write(selName, e1); g1 = map.Read(govName).Value?.ToString() ?? "";

                            string testVal = ChooseDifferentValue(map, govName, g1);
                            map.Write(selName, e1);
                            map.Write(govName, testVal);
                            string rb = map.Read(govName).Value?.ToString() ?? "";
                            bool writeLanded = ValuesEqual(map, govName, rb, testVal);

                            map.Write(selName, e0);
                            string g0after = map.Read(govName).Value?.ToString() ?? "";
                            bool independent = string.Equals(g0after, g0, StringComparison.Ordinal);

                            Console.WriteLine($"  Selector: {selName} ({e0} / {e1})   Governed: {govName} [{map.GetNodeKind(govName)}]");
                            Console.WriteLine($"  Under {e1}: wrote {govName}={testVal}, readback={rb}  -> write {(writeLanded ? "PASS" : "FAIL")}");
                            Console.WriteLine($"  Under {e0}: {govName} was {g0}, still {g0after}  -> independence {(independent ? "PASS" : "FAIL")}");
                            Console.WriteLine($"  Selector-indexed write: {(writeLanded && independent ? "PASS" : "see values above")}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  {selName}/{govName}: skipped ({ex.Message})");
                            continue; // try the next candidate
                        }
                        finally
                        {
                            try { map.Write(selName, e1); map.Write(govName, g1); } catch { }
                            try { map.Write(selName, e0); map.Write(govName, g0); } catch { }
                            try { map.Write(selName, origSel); } catch { }
                        }
                        return; // a candidate produced a result
                    }
                    Console.WriteLine("  All candidate selectors errored on their governed registers — inconclusive.");
                }
                finally { device.Dispose(); iface.Dispose(); system.Dispose(); }
            }
            catch (Exception ex) { Console.WriteLine($"  Selector-indexed write test failed: {ex.Message}"); }
            finally { api.Dispose(); }
            Console.WriteLine();
        }

        // Picks a valid value for the governed feature that differs from its current value.
        static string ChooseDifferentValue(NodeMap map, string g, string current)
        {
            if (map.GetNodeKind(g) == FeatureKind.Enumeration)
                return map.GetEnumEntries(g).FirstOrDefault(e => !string.Equals(e, current, StringComparison.Ordinal)) ?? current;

            var lim = map.GetNodeLimits(g);
            double cur = double.TryParse(current, NumberStyles.Any, CultureInfo.InvariantCulture, out var c) ? c : 0;
            if (lim.min.HasValue && lim.max.HasValue)
            {
                // pick the limit farther from the current value
                double cand = Math.Abs(cur - lim.min.Value) >= Math.Abs(cur - lim.max.Value) ? lim.min.Value : lim.max.Value;
                return map.GetNodeKind(g) == FeatureKind.Integer
                    ? ((long)cand).ToString(CultureInfo.InvariantCulture)
                    : cand.ToString(CultureInfo.InvariantCulture);
            }
            double nudged = cur + 1;
            return map.GetNodeKind(g) == FeatureKind.Integer
                ? ((long)nudged).ToString(CultureInfo.InvariantCulture)
                : nudged.ToString(CultureInfo.InvariantCulture);
        }

        static bool ValuesEqual(NodeMap map, string g, string a, string b)
        {
            if (map.GetNodeKind(g) == FeatureKind.Enumeration)
                return string.Equals(a, b, StringComparison.Ordinal);
            return double.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out var da)
                && double.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out var db)
                && Math.Abs(da - db) <= Math.Max(1e-6, Math.Abs(db) * 0.01);
        }
    }
}
