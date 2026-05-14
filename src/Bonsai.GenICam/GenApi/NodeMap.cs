using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Bonsai.GenICam.GenTL;
using Bonsai.GenICam;

namespace Bonsai.GenICam.GenApi
{
    // Fetches the device's GenICam XML description, parses it into a node tree,
    // and provides typed read/write access by feature name.
    internal class NodeMap
    {
        private readonly GenTLApi _api;
        private readonly IntPtr _port;
        private readonly Dictionary<string, NodeBase> _nodes = new Dictionary<string, NodeBase>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _categories = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _categoryDescriptions = new Dictionary<string, string>(StringComparer.Ordinal);

        internal NodeMap(GenTLApi api, IntPtr port)
        {
            _api = api;
            _port = port;
            var xml = FetchXml();
            ParseXml(xml);
        }

        private string FetchXml()
        {
            string url = GenTLApi.FetchStringRef(delegate(byte[] buf, ref UIntPtr sz) {
                return _api.GCGetPortURL(_port, buf, ref sz);
            });

            if (url.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
            {
                // Format: "local:FileName.xml;HexAddress;HexLength"
                var parts = url.Substring(6).Split(';');
                if (parts.Length < 3)
                    throw new InvalidOperationException($"Unexpected local: URL format: {url}");

                ulong address = ParseHex(parts[1]);
                int length = (int)ParseHex(parts[2]);

                var bytes = new byte[length];
                var size = new UIntPtr((uint)length);
                GenTLException.Check(_api.GCReadPort(_port, address, bytes, ref size));

                return DecompressOrDecode(bytes, parts[0], url);
            }
            else if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return File.ReadAllText(new Uri(url).LocalPath, Encoding.UTF8);
            }
            else
            {
                throw new NotSupportedException($"Unsupported GenICam XML URL scheme: {url}");
            }
        }

        private static ulong ParseHex(string s)
        {
            s = s.Trim();
            // Strip optional query string (e.g. "116bf?SchemaVersion=1.0.0" from HikRobot)
            int q = s.IndexOf('?');
            if (q >= 0) s = s.Substring(0, q);
            // Strip optional 0x / &H prefix
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("&H", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);
            return Convert.ToUInt64(s, 16);
        }

        private static string DecompressOrDecode(byte[] bytes, string filename, string url)
        {
            // ZIP: PK magic (0x50 0x4B) or .zip extension
            if ((bytes.Length >= 2 && bytes[0] == 0x50 && bytes[1] == 0x4B) ||
                filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using (var ms = new MemoryStream(bytes))
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    if (archive.Entries.Count == 0)
                        throw new InvalidOperationException($"GenICam XML ZIP at '{url}' contains no entries.");
                    using (var reader = new StreamReader(archive.Entries[0].Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false))
                        return reader.ReadToEnd();
                }
            }

            // GZIP: magic bytes 1F 8B
            if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using (var ms = new MemoryStream(bytes))
                using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                using (var reader = new StreamReader(gz, Encoding.UTF8))
                    return reader.ReadToEnd();
            }

            // Plain text — skip UTF-8 BOM if present
            int offset = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        }

        private void ParseXml(string xmlText)
        {
            var doc = XDocument.Parse(xmlText);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            // Use Descendants() rather than Elements() so that non-standard producers
            // (e.g. HikRobot MVS) whose feature nodes are embedded inside <Category>
            // children are discovered correctly. Standard GenICam XML is flat (all nodes
            // are direct children of <RegisterDescription>), but this handles both.
            foreach (var el in doc.Root?.Descendants() ?? System.Linq.Enumerable.Empty<XElement>())
            {
                string name = (string)el.Attribute("Name");
                if (string.IsNullOrEmpty(name)) continue;

                if (el.Name.LocalName == "Category")
                {
                    var features = new List<string>();
                    foreach (var pf in el.Elements(ns + "pFeature"))
                        if (!string.IsNullOrEmpty(pf.Value)) features.Add(pf.Value.Trim());
                    if (features.Count > 0) _categories[name] = features;
                    _categoryDescriptions[name] = ((string)el.Element(ns + "Description") ?? (string)el.Element(ns + "ToolTip") ?? "").Trim();
                    continue;
                }

                NodeBase? node = ParseElement(el, ns, name);
                if (node != null)
                {
                    node.Description = ((string)el.Element(ns + "Description") ?? (string)el.Element(ns + "ToolTip"))?.Trim();
                    _nodes[name] = node;
                }
            }
        }

        private static string[]? ParsePAddresses(XElement el, XNamespace ns)
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var pa in el.Elements(ns + "pAddress"))
                if (!string.IsNullOrEmpty(pa.Value)) list.Add(pa.Value.Trim());
            return list.Count > 0 ? list.ToArray() : null;
        }

        private static NodeBase? ParseElement(XElement el, XNamespace ns, string name)
        {
            var accessMode = ParseAccessMode((string)el.Element(ns + "AccessMode") ?? (string)el.Attribute("AccessMode") ?? "RW");

            switch (el.Name.LocalName)
            {
                case "IntReg":
                    return new IntRegNode
                    {
                        Name = name,
                        AccessMode = accessMode,
                        Address = ParseAddress(el, ns),
                        PAddresses = ParsePAddresses(el, ns),
                        Length = ParseInt(el, ns, "Length", 4),
                        Unsigned = !string.Equals((string)el.Element(ns + "Sign"), "Signed", StringComparison.OrdinalIgnoreCase),
                        LittleEndian = !string.Equals((string)el.Element(ns + "Endianess"), "BigEndian", StringComparison.OrdinalIgnoreCase)
                    };

                case "MaskedIntReg":
                {
                    // GenICam allows <Bit>n</Bit> as shorthand for <Mask>1<<n</Mask> <Shift>n</Shift>
                    string bitStr = (string)el.Element(ns + "Bit");
                    ulong mask; int shift;
                    if (bitStr != null)
                    {
                        int bit = int.Parse(bitStr.Trim());
                        mask = 1UL << bit; shift = bit;
                    }
                    else
                    {
                        mask = ParseULong(el, ns, "Mask", ulong.MaxValue);
                        shift = ParseInt(el, ns, "Shift", 0);
                    }
                    return new MaskedIntRegNode
                    {
                        Name = name,
                        AccessMode = accessMode,
                        Address = ParseAddress(el, ns),
                        PAddresses = ParsePAddresses(el, ns),
                        Length = ParseInt(el, ns, "Length", 4),
                        Mask = mask,
                        Shift = shift,
                        Unsigned = !string.Equals((string)el.Element(ns + "Sign"), "Signed", StringComparison.OrdinalIgnoreCase),
                        LittleEndian = !string.Equals((string)el.Element(ns + "Endianess"), "BigEndian", StringComparison.OrdinalIgnoreCase)
                    };
                }

                case "FloatReg":
                    return new FloatRegNode
                    {
                        Name = name,
                        AccessMode = accessMode,
                        Address = ParseAddress(el, ns),
                        PAddresses = ParsePAddresses(el, ns),
                        Length = ParseInt(el, ns, "Length", 4)
                    };

                case "StringReg":
                    return new StringRegNode
                    {
                        Name = name,
                        AccessMode = accessMode,
                        Address = ParseAddress(el, ns),
                        PAddresses = ParsePAddresses(el, ns),
                        Length = ParseInt(el, ns, "Length", 64)
                    };

                case "Integer":
                {
                    string pv = (string)el.Element(ns + "pValue");
                    string val = (string)el.Element(ns + "Value");
                    if (pv == null && val != null)
                        return new IntegerNode { Name = name, AccessMode = NodeAccessMode.RO, ConstantValue = ParseLongLiteral(val) };
                    string minS = (string)el.Element(ns + "Min"), maxS = (string)el.Element(ns + "Max"), incS = (string)el.Element(ns + "Inc");
                    return new IntegerNode
                    {
                        Name = name, AccessMode = accessMode, PValue = pv,
                        LiteralMin = minS != null ? (long?)ParseLongLiteral(minS) : null,
                        LiteralMax = maxS != null ? (long?)ParseLongLiteral(maxS) : null,
                        LiteralInc = incS != null ? (long?)ParseLongLiteral(incS) : null,
                        PMin = (string)el.Element(ns + "pMin"),
                        PMax = (string)el.Element(ns + "pMax"),
                        PInc = (string)el.Element(ns + "pInc")
                    };
                }

                case "Float":
                {
                    string minS = (string)el.Element(ns + "Min"), maxS = (string)el.Element(ns + "Max");
                    return new FloatNode
                    {
                        Name = name, AccessMode = accessMode,
                        PValue = (string)el.Element(ns + "pValue"),
                        LiteralMin = minS != null ? (double?)double.Parse(minS.Trim(), System.Globalization.CultureInfo.InvariantCulture) : null,
                        LiteralMax = maxS != null ? (double?)double.Parse(maxS.Trim(), System.Globalization.CultureInfo.InvariantCulture) : null,
                        PMin = (string)el.Element(ns + "pMin"),
                        PMax = (string)el.Element(ns + "pMax")
                    };
                }

                case "String":
                    return new StringNode
                    {
                        Name = name,
                        AccessMode = accessMode,
                        PValue = (string)el.Element(ns + "pValue")
                    };

                case "Boolean":
                    return new BooleanNode
                    {
                        Name = name,
                        AccessMode = accessMode,
                        PValue = (string)el.Element(ns + "pValue")
                    };

                case "Command":
                    return new CommandNode
                    {
                        Name = name,
                        AccessMode = accessMode,
                        PValue = (string)el.Element(ns + "pValue"),
                        CommandValue = ParseLong(el, ns, "CommandValue", 1)
                    };

                case "Enumeration":
                {
                    var entries = new Dictionary<string, long>(StringComparer.Ordinal);
                    var byValue = new Dictionary<long, string>();
                    foreach (var entry in el.Elements(ns + "EnumEntry"))
                    {
                        string entryName = (string)entry.Attribute("Name");
                        long value = ParseLong(entry, ns, "Value", 0);
                        if (entryName != null)
                        {
                            entries[entryName] = value;
                            byValue[value] = entryName;
                        }
                    }
                    return new EnumerationNode
                    {
                        Name = name,
                        AccessMode = accessMode,
                        PValue = (string)el.Element(ns + "pValue"),
                        Entries = entries,
                        SymbolicByValue = byValue
                    };
                }

                case "IntSwissKnife":
                {
                    var vars = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var v in el.Elements(ns + "pVariable"))
                    {
                        string varName = (string)v.Attribute("Name");
                        if (varName != null) vars[varName] = v.Value.Trim();
                    }
                    return new IntSwissKnifeNode
                    {
                        Name = name,
                        AccessMode = NodeAccessMode.RO,
                        Variables = vars,
                        Formula = (string)el.Element(ns + "Formula") ?? ""
                    };
                }

                case "SwissKnife":
                {
                    var vars = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var v in el.Elements(ns + "pVariable"))
                    {
                        string varName = (string)v.Attribute("Name");
                        if (varName != null) vars[varName] = v.Value.Trim();
                    }
                    return new SwissKnifeNode
                    {
                        Name = name,
                        AccessMode = NodeAccessMode.RO,
                        Variables = vars,
                        Formula = (string)el.Element(ns + "Formula") ?? ""
                    };
                }

                case "Converter":
                    return new ConverterNode
                    {
                        Name = name,
                        AccessMode = accessMode,
                        PValue = (string)el.Element(ns + "pValue"),
                        FormulaTo = (string)el.Element(ns + "FormulaTo") ?? "FROM",
                        FormulaFrom = (string)el.Element(ns + "FormulaFrom") ?? "TO"
                    };

                case "IntConverter":
                    return new IntConverterNode
                    {
                        Name = name,
                        AccessMode = accessMode,
                        PValue = (string)el.Element(ns + "pValue"),
                        FormulaTo = (string)el.Element(ns + "FormulaTo") ?? "FROM",
                        FormulaFrom = (string)el.Element(ns + "FormulaFrom") ?? "TO"
                    };

                default:
                    return null;
            }
        }

        // ---- public read/write API ----

        internal FeatureValue Read(string name)
        {
            var node = Resolve(name);
            return new FeatureValue(name, ReadNode(node));
        }

        internal void Write(string name, string valueStr)
        {
            var node = Resolve(name);
            WriteNode(node, valueStr);
        }

        internal bool CanWrite(string name)
        {
            var node = Resolve(name);
            return node.AccessMode == NodeAccessMode.RW || node.AccessMode == NodeAccessMode.WO;
        }

        // Returns category name → list of feature names (from <pFeature> elements).
        internal IReadOnlyDictionary<string, IReadOnlyList<string>> GetCategories()
        {
            var result = new Dictionary<string, IReadOnlyList<string>>(_categories.Count, StringComparer.Ordinal);
            foreach (var kv in _categories)
                result[kv.Key] = kv.Value;
            return result;
        }

        // Returns the Description (or ToolTip) text for a named category or feature node.
        internal string GetCategoryDescription(string name)
            => _categoryDescriptions.TryGetValue(name, out string d) ? d : "";

        internal string GetNodeDescription(string name)
            => _nodes.TryGetValue(name, out var node) ? node.Description ?? "" : "";

        internal FeatureKind GetNodeKind(string name)
        {
            if (!_nodes.TryGetValue(name, out var node)) return FeatureKind.Text;
            switch (node)
            {
                case EnumerationNode _: return FeatureKind.Enumeration;
                case BooleanNode _:     return FeatureKind.Boolean;
                case CommandNode _:     return FeatureKind.Command;
                case FloatRegNode _:
                case FloatNode _:
                case SwissKnifeNode _:
                case ConverterNode _:   return FeatureKind.Float;
                case StringRegNode _:
                case StringNode _:      return FeatureKind.Text;
                default:                return FeatureKind.Integer;
            }
        }

        internal IReadOnlyList<string> GetEnumEntries(string name)
        {
            if (_nodes.TryGetValue(name, out var node) && node is EnumerationNode en)
            {
                var list = new List<string>(en.Entries.Count);
                foreach (string key in en.Entries.Keys) list.Add(key);
                return list;
            }
            return new string[0];
        }

        internal IEnumerable<string> GetCommandNodeNames()
        {
            foreach (var kv in _nodes)
                if (kv.Value is CommandNode) yield return kv.Key;
        }

        internal (double? min, double? max, double? step) GetNodeLimits(string name)
        {
            if (!_nodes.TryGetValue(name, out var node)) return (null, null, null);
            double? TryReadRef(string refName) { try { return Convert.ToDouble(ReadNode(Resolve(refName))); } catch { return null; } }

            if (node is IntegerNode n)
            {
                double? min  = n.LiteralMin.HasValue ? (double?)n.LiteralMin.Value : (n.PMin != null ? TryReadRef(n.PMin) : null);
                double? max  = n.LiteralMax.HasValue ? (double?)n.LiteralMax.Value : (n.PMax != null ? TryReadRef(n.PMax) : null);
                double? step = n.LiteralInc.HasValue ? (double?)n.LiteralInc.Value : (n.PInc != null ? TryReadRef(n.PInc) : null);
                return (min, max, step);
            }
            if (node is FloatNode f)
            {
                double? min = f.LiteralMin.HasValue ? (double?)f.LiteralMin.Value : (f.PMin != null ? TryReadRef(f.PMin) : null);
                double? max = f.LiteralMax.HasValue ? (double?)f.LiteralMax.Value : (f.PMax != null ? TryReadRef(f.PMax) : null);
                return (min, max, null);
            }
            return (null, null, null);
        }

        // Returns all node names whose values can be read without error.
        internal IEnumerable<FeatureValue> TryReadAll()
        {
            foreach (var kv in _nodes)
            {
                object value;
                try { value = ReadNode(kv.Value); }
                catch { continue; } // skip unreadable / WO / computed nodes
                yield return new FeatureValue(kv.Key, value);
            }
        }

        // ---- internals ----

        private NodeBase Resolve(string name)
        {
            if (_nodes.TryGetValue(name, out var node)) return node;
            throw new KeyNotFoundException($"GenICam feature '{name}' not found in device XML.");
        }

        private NodeBase ResolveRef(string? refName)
        {
            if (refName == null) throw new InvalidOperationException("Null pValue reference.");
            return Resolve(refName);
        }

        private object ReadNode(NodeBase node)
        {
            switch (node)
            {
                case MaskedIntRegNode r: return ReadMaskedIntReg(r);
                case IntRegNode r: return ReadIntReg(r);
                case FloatRegNode r: return ReadFloatReg(r);
                case StringRegNode r: return ReadStringReg(r);
                case IntegerNode n:
                    if (n.ConstantValue.HasValue) return n.ConstantValue.Value;
                    return ReadNode(ResolveRef(n.PValue));
                case FloatNode n: return ReadNode(ResolveRef(n.PValue));
                case StringNode n: return ReadNode(ResolveRef(n.PValue));
                case BooleanNode n: return Convert.ToInt64(ReadNode(ResolveRef(n.PValue))) != 0;
                case EnumerationNode n:
                {
                    long val = Convert.ToInt64(ReadNode(ResolveRef(n.PValue)));
                    return n.SymbolicByValue.TryGetValue(val, out string sym) ? (object)sym : val;
                }
                case CommandNode _:
                    throw new InvalidOperationException("Command nodes cannot be read.");
                case IntSwissKnifeNode n:
                {
                    var vars = ResolveFormulaVars(n.Variables);
                    return (long)EvaluateFormula(n.Formula, vars);
                }
                case SwissKnifeNode n:
                {
                    var vars = ResolveFormulaVars(n.Variables);
                    return EvaluateFormula(n.Formula, vars);
                }
                case ConverterNode n:
                {
                    double from = Convert.ToDouble(ReadNode(ResolveRef(n.PValue)));
                    var vars = new Dictionary<string, double>(StringComparer.Ordinal) { ["FROM"] = from };
                    return EvaluateFormula(n.FormulaTo, vars);
                }
                case IntConverterNode n:
                {
                    double from = Convert.ToDouble(ReadNode(ResolveRef(n.PValue)));
                    var vars = new Dictionary<string, double>(StringComparer.Ordinal) { ["FROM"] = from };
                    return (long)EvaluateFormula(n.FormulaTo, vars);
                }
                default:
                    throw new NotSupportedException($"Unsupported node type: {node.GetType().Name}");
            }
        }

        private void WriteNode(NodeBase node, string valueStr)
        {
            switch (node)
            {
                case MaskedIntRegNode r:
                    WriteMaskedIntReg(r, Convert.ToInt64(valueStr));
                    break;
                case IntRegNode r:
                    WriteIntReg(r, Convert.ToInt64(valueStr));
                    break;
                case FloatRegNode r:
                    WriteFloatReg(r, Convert.ToDouble(valueStr));
                    break;
                case StringRegNode r:
                    WriteStringReg(r, valueStr);
                    break;
                case IntegerNode n:
                    if (n.ConstantValue.HasValue)
                        throw new InvalidOperationException($"Feature '{n.Name}' is a read-only constant.");
                    WriteNode(ResolveRef(n.PValue), valueStr);
                    break;
                case FloatNode n:
                    WriteNode(ResolveRef(n.PValue), valueStr);
                    break;
                case StringNode n:
                    WriteNode(ResolveRef(n.PValue), valueStr);
                    break;
                case BooleanNode n:
                {
                    long v = (valueStr.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                              valueStr == "1") ? 1L : 0L;
                    WriteNode(ResolveRef(n.PValue), v.ToString());
                    break;
                }
                case CommandNode n:
                    WriteNode(ResolveRef(n.PValue), n.CommandValue.ToString());
                    break;
                case EnumerationNode n:
                {
                    if (n.Entries.TryGetValue(valueStr, out long enumVal))
                        WriteNode(ResolveRef(n.PValue), enumVal.ToString());
                    else
                        WriteNode(ResolveRef(n.PValue), valueStr); // allow raw int
                    break;
                }
                case IntSwissKnifeNode _:
                case SwissKnifeNode _:
                    throw new InvalidOperationException("SwissKnife nodes are read-only.");
                case ConverterNode n:
                {
                    double to = Convert.ToDouble(valueStr);
                    var vars = new Dictionary<string, double>(StringComparer.Ordinal) { ["TO"] = to };
                    double raw = EvaluateFormula(n.FormulaFrom, vars);
                    WriteNode(ResolveRef(n.PValue), raw.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                }
                case IntConverterNode n:
                {
                    double to = Convert.ToDouble(valueStr);
                    var vars = new Dictionary<string, double>(StringComparer.Ordinal) { ["TO"] = to };
                    long raw = (long)EvaluateFormula(n.FormulaFrom, vars);
                    WriteNode(ResolveRef(n.PValue), raw.ToString());
                    break;
                }
                default:
                    throw new NotSupportedException($"Unsupported node type: {node.GetType().Name}");
            }
        }

        // ---- register read/write ----

        private ulong ResolveAddress(IntRegNode r)
        {
            ulong addr = r.Address;
            if (r.PAddresses != null)
                foreach (string pa in r.PAddresses)
                    addr += (ulong)Convert.ToInt64(ReadNode(Resolve(pa)));
            return addr;
        }

        private ulong ResolveAddressFloat(FloatRegNode r)
        {
            ulong addr = r.Address;
            if (r.PAddresses != null)
                foreach (string pa in r.PAddresses)
                    addr += (ulong)Convert.ToInt64(ReadNode(Resolve(pa)));
            return addr;
        }

        private ulong ResolveAddressString(StringRegNode r)
        {
            ulong addr = r.Address;
            if (r.PAddresses != null)
                foreach (string pa in r.PAddresses)
                    addr += (ulong)Convert.ToInt64(ReadNode(Resolve(pa)));
            return addr;
        }

        private long ReadIntReg(IntRegNode r)
        {
            ulong address = ResolveAddress(r);
            var buf = ReadPort(address, r.Length);
            ulong raw = ToUInt64(buf, r.Length, r.LittleEndian);
            return r.Unsigned ? (long)raw : SignExtend(raw, r.Length);
        }

        private long ReadMaskedIntReg(MaskedIntRegNode r)
        {
            long raw = ReadIntReg(r);
            return (long)(((ulong)raw & r.Mask) >> r.Shift);
        }

        private double ReadFloatReg(FloatRegNode r)
        {
            ulong address = ResolveAddressFloat(r);
            var buf = ReadPort(address, r.Length);
            return r.Length == 4
                ? BitConverter.ToSingle(r.LittleEndian ? buf : Reverse(buf), 0)
                : BitConverter.ToDouble(r.LittleEndian ? buf : Reverse(buf), 0);
        }

        private string ReadStringReg(StringRegNode r)
        {
            ulong address = ResolveAddressString(r);
            var buf = ReadPort(address, r.Length);
            int len = Array.IndexOf(buf, (byte)0);
            return Encoding.ASCII.GetString(buf, 0, len < 0 ? buf.Length : len);
        }

        private void WriteIntReg(IntRegNode r, long value)
        {
            ulong address = ResolveAddress(r);
            var buf = FromUInt64((ulong)value, r.Length, r.LittleEndian);
            WritePort(address, buf);
        }

        private void WriteMaskedIntReg(MaskedIntRegNode r, long value)
        {
            long current = ReadIntReg(r);
            long masked = (long)(((ulong)current & ~r.Mask) | (((ulong)value << r.Shift) & r.Mask));
            WriteIntReg(r, masked);
        }

        private void WriteFloatReg(FloatRegNode r, double value)
        {
            ulong address = ResolveAddressFloat(r);
            byte[] buf = r.Length == 4
                ? BitConverter.GetBytes((float)value)
                : BitConverter.GetBytes(value);
            if (!r.LittleEndian) Array.Reverse(buf);
            WritePort(address, buf);
        }

        private void WriteStringReg(StringRegNode r, string value)
        {
            ulong address = ResolveAddressString(r);
            var bytes = new byte[r.Length];
            var encoded = Encoding.ASCII.GetBytes(value);
            int copy = Math.Min(encoded.Length, r.Length - 1);
            Array.Copy(encoded, bytes, copy);
            WritePort(address, bytes);
        }

        private byte[] ReadPort(ulong address, int length)
        {
            var buf = new byte[length];
            var size = new UIntPtr((uint)length);
            GenTLException.Check(_api.GCReadPort(_port, address, buf, ref size));
            return buf;
        }

        private void WritePort(ulong address, byte[] data)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var size = new UIntPtr((uint)data.Length);
                int err = _api.GCWritePort(_port, address, handle.AddrOfPinnedObject(), ref size);
                GenTLException.Check(err);
            }
            finally
            {
                handle.Free();
            }
        }

        // ---- XML helpers ----

        private static ulong ParseAddress(XElement el, XNamespace ns)
        {
            string val = (string)el.Element(ns + "Address");
            if (val == null) return 0;
            return val.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt64(val, 16)
                : ulong.Parse(val);
        }

        private static long ParseLongLiteral(string s)
        {
            s = s.Trim();
            return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? (long)Convert.ToUInt64(s, 16)
                : long.Parse(s);
        }

        private static int ParseInt(XElement el, XNamespace ns, string localName, int defaultValue)
        {
            string val = (string)el.Element(ns + localName);
            return val == null ? defaultValue : int.Parse(val);
        }

        private static long ParseLong(XElement el, XNamespace ns, string localName, long defaultValue)
        {
            string val = (string)el.Element(ns + localName);
            if (val == null) return defaultValue;
            return val.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? (long)Convert.ToUInt64(val, 16)
                : long.Parse(val);
        }

        private static ulong ParseULong(XElement el, XNamespace ns, string localName, ulong defaultValue)
        {
            string val = (string)el.Element(ns + localName);
            if (val == null) return defaultValue;
            return val.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt64(val, 16)
                : ulong.Parse(val);
        }

        private static NodeAccessMode ParseAccessMode(string s)
        {
            switch (s?.ToUpperInvariant())
            {
                case "RW": return NodeAccessMode.RW;
                case "RO": return NodeAccessMode.RO;
                case "WO": return NodeAccessMode.WO;
                case "NA": return NodeAccessMode.NA;
                default: return NodeAccessMode.RW;
            }
        }

        private static ulong ToUInt64(byte[] buf, int length, bool littleEndian)
        {
            if (!littleEndian) buf = Reverse(buf);
            ulong val = 0;
            for (int i = 0; i < length && i < 8; i++)
                val |= (ulong)buf[i] << (i * 8);
            return val;
        }

        private static byte[] FromUInt64(ulong value, int length, bool littleEndian)
        {
            var buf = new byte[length];
            for (int i = 0; i < length && i < 8; i++)
                buf[i] = (byte)(value >> (i * 8));
            if (!littleEndian) Array.Reverse(buf);
            return buf;
        }

        private static long SignExtend(ulong raw, int byteLength)
        {
            int bits = byteLength * 8;
            ulong signBit = 1UL << (bits - 1);
            if ((raw & signBit) != 0)
                raw |= ~((1UL << bits) - 1);
            return (long)raw;
        }

        private static byte[] Reverse(byte[] buf)
        {
            var copy = (byte[])buf.Clone();
            Array.Reverse(copy);
            return copy;
        }

        // ---- formula evaluation ----

        private Dictionary<string, double> ResolveFormulaVars(Dictionary<string, string> variables)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var kv in variables)
            {
                if (_nodes.TryGetValue(kv.Value, out var refNode))
                    result[kv.Key] = Convert.ToDouble(ReadNode(refNode));
            }
            return result;
        }

        private static double EvaluateFormula(string? formula, Dictionary<string, double> vars)
        {
            if (formula == null) throw new InvalidOperationException("Null formula reference.");
            return new FormulaEvaluator(formula, vars).ParseTernary();
        }

        private sealed class FormulaEvaluator
        {
            private readonly string _s;
            private readonly Dictionary<string, double> _vars;
            private int _i;

            internal FormulaEvaluator(string s, Dictionary<string, double> vars)
            {
                _s = s;
                _vars = vars;
                _i = 0;
            }

            internal double ParseTernary()
            {
                double cond = ParseOr();
                Skip();
                if (_i < _s.Length && _s[_i] == '?')
                {
                    _i++;
                    double t = ParseTernary();
                    Skip();
                    if (_i >= _s.Length || _s[_i] != ':')
                        throw new InvalidOperationException($"Expected ':' in ternary at pos {_i}: {_s}");
                    _i++;
                    double f = ParseTernary();
                    return cond != 0 ? t : f;
                }
                return cond;
            }

            private double ParseOr()
            {
                double left = ParseAnd();
                while (true)
                {
                    Skip();
                    if (_i + 1 < _s.Length && _s[_i] == '|' && _s[_i + 1] == '|')
                    { _i += 2; double r = ParseAnd(); left = (left != 0 || r != 0) ? 1 : 0; }
                    else break;
                }
                return left;
            }

            private double ParseAnd()
            {
                double left = ParseBitOr();
                while (true)
                {
                    Skip();
                    if (_i + 1 < _s.Length && _s[_i] == '&' && _s[_i + 1] == '&')
                    { _i += 2; double r = ParseBitOr(); left = (left != 0 && r != 0) ? 1 : 0; }
                    else break;
                }
                return left;
            }

            private double ParseBitOr()
            {
                double left = ParseBitXor();
                while (true)
                {
                    Skip();
                    if (_i < _s.Length && _s[_i] == '|' &&
                        (_i + 1 >= _s.Length || _s[_i + 1] != '|'))
                    { _i++; left = (long)left | (long)ParseBitXor(); }
                    else break;
                }
                return left;
            }

            private double ParseBitXor()
            {
                double left = ParseBitAnd();
                while (true)
                {
                    Skip();
                    if (_i < _s.Length && _s[_i] == '^')
                    { _i++; left = (long)left ^ (long)ParseBitAnd(); }
                    else break;
                }
                return left;
            }

            private double ParseBitAnd()
            {
                double left = ParseEquality();
                while (true)
                {
                    Skip();
                    if (_i < _s.Length && _s[_i] == '&' &&
                        (_i + 1 >= _s.Length || _s[_i + 1] != '&'))
                    { _i++; left = (long)left & (long)ParseEquality(); }
                    else break;
                }
                return left;
            }

            private double ParseEquality()
            {
                double left = ParseRelational();
                while (true)
                {
                    Skip();
                    if (_i + 1 < _s.Length && _s[_i] == '=' && _s[_i + 1] == '=')
                    { _i += 2; left = (left == ParseRelational()) ? 1 : 0; }
                    else if (_i + 1 < _s.Length && _s[_i] == '!' && _s[_i + 1] == '=')
                    { _i += 2; left = (left != ParseRelational()) ? 1 : 0; }
                    else break;
                }
                return left;
            }

            private double ParseRelational()
            {
                double left = ParseShift();
                while (true)
                {
                    Skip();
                    if (_i + 1 < _s.Length && _s[_i] == '<' && _s[_i + 1] == '=')
                    { _i += 2; left = (left <= ParseShift()) ? 1 : 0; }
                    else if (_i + 1 < _s.Length && _s[_i] == '>' && _s[_i + 1] == '=')
                    { _i += 2; left = (left >= ParseShift()) ? 1 : 0; }
                    else if (_i < _s.Length && _s[_i] == '<' &&
                             (_i + 1 >= _s.Length || _s[_i + 1] != '<'))
                    { _i++; left = (left < ParseShift()) ? 1 : 0; }
                    else if (_i < _s.Length && _s[_i] == '>' &&
                             (_i + 1 >= _s.Length || (_s[_i + 1] != '>' && _s[_i + 1] != '=')))
                    { _i++; left = (left > ParseShift()) ? 1 : 0; }
                    else break;
                }
                return left;
            }

            private double ParseShift()
            {
                double left = ParseAdditive();
                while (true)
                {
                    Skip();
                    if (_i + 1 < _s.Length && _s[_i] == '<' && _s[_i + 1] == '<')
                    { _i += 2; left = (long)left << (int)ParseAdditive(); }
                    else if (_i + 1 < _s.Length && _s[_i] == '>' && _s[_i + 1] == '>')
                    { _i += 2; left = (long)left >> (int)ParseAdditive(); }
                    else break;
                }
                return left;
            }

            private double ParseAdditive()
            {
                double left = ParseMultiplicative();
                while (true)
                {
                    Skip();
                    if (_i < _s.Length && _s[_i] == '+')
                    { _i++; left += ParseMultiplicative(); }
                    else if (_i < _s.Length && _s[_i] == '-')
                    { _i++; left -= ParseMultiplicative(); }
                    else break;
                }
                return left;
            }

            private double ParseMultiplicative()
            {
                double left = ParsePower();
                while (true)
                {
                    Skip();
                    if (_i < _s.Length && _s[_i] == '*' &&
                        (_i + 1 >= _s.Length || _s[_i + 1] != '*'))
                    { _i++; left *= ParsePower(); }
                    else if (_i < _s.Length && _s[_i] == '/')
                    { _i++; left /= ParsePower(); }
                    else if (_i < _s.Length && _s[_i] == '%')
                    { _i++; left = (long)left % (long)ParsePower(); }
                    else break;
                }
                return left;
            }

            private double ParsePower()
            {
                double left = ParseUnary();
                Skip();
                if (_i + 1 < _s.Length && _s[_i] == '*' && _s[_i + 1] == '*')
                {
                    _i += 2;
                    return Math.Pow(left, ParsePower()); // right-associative
                }
                return left;
            }

            private double ParseUnary()
            {
                Skip();
                if (_i < _s.Length && _s[_i] == '-') { _i++; return -ParseUnary(); }
                if (_i < _s.Length && _s[_i] == '+') { _i++; return ParseUnary(); }
                if (_i < _s.Length && _s[_i] == '~') { _i++; return (double)(~(long)ParseUnary()); }
                if (_i < _s.Length && _s[_i] == '!') { _i++; return ParseUnary() == 0 ? 1 : 0; }
                return ParsePrimary();
            }

            private double ParsePrimary()
            {
                Skip();
                if (_i >= _s.Length)
                    throw new InvalidOperationException($"Unexpected end of formula: {_s}");

                if (char.IsDigit(_s[_i]) || (_s[_i] == '.' && _i + 1 < _s.Length && char.IsDigit(_s[_i + 1])))
                    return ParseNumber();

                if (_s[_i] == '(')
                {
                    _i++;
                    double val = ParseTernary();
                    Skip();
                    if (_i >= _s.Length || _s[_i] != ')')
                        throw new InvalidOperationException($"Expected ')' at pos {_i}: {_s}");
                    _i++;
                    return val;
                }

                if (char.IsLetter(_s[_i]) || _s[_i] == '_')
                {
                    int start = _i;
                    while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_'))
                        _i++;
                    string id = _s.Substring(start, _i - start);
                    Skip();
                    if (_i < _s.Length && _s[_i] == '(')
                    {
                        _i++;
                        double arg = ParseTernary();
                        Skip();
                        if (_i >= _s.Length || _s[_i] != ')')
                            throw new InvalidOperationException($"Expected ')' after function at pos {_i}: {_s}");
                        _i++;
                        return EvalFunction(id, arg);
                    }
                    if (_vars.TryGetValue(id, out double v)) return v;
                    if (id == "PI") return Math.PI;
                    if (id == "E") return Math.E;
                    throw new InvalidOperationException($"Unknown variable '{id}' in formula: {_s}");
                }

                throw new InvalidOperationException($"Unexpected character '{_s[_i]}' at pos {_i} in formula: {_s}");
            }

            private double ParseNumber()
            {
                int start = _i;
                if (_i + 1 < _s.Length && _s[_i] == '0' && (_s[_i + 1] == 'x' || _s[_i + 1] == 'X'))
                {
                    _i += 2;
                    while (_i < _s.Length && IsHexDigit(_s[_i])) _i++;
                    return (double)Convert.ToUInt64(_s.Substring(start, _i - start), 16);
                }
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                if (_i < _s.Length && _s[_i] == '.')
                {
                    _i++;
                    while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                }
                if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    _i++;
                    if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                    while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                }
                return double.Parse(_s.Substring(start, _i - start), System.Globalization.CultureInfo.InvariantCulture);
            }

            private static double EvalFunction(string name, double arg)
            {
                switch (name.ToUpperInvariant())
                {
                    case "SGN":   return arg < 0 ? -1 : arg > 0 ? 1 : 0;
                    case "NEG":   return -arg;
                    case "ABS":   return Math.Abs(arg);
                    case "SQRT":  return Math.Sqrt(arg);
                    case "FLOOR": return Math.Floor(arg);
                    case "CEIL":  return Math.Ceiling(arg);
                    case "ROUND": return Math.Round(arg);
                    case "TRUNC": return Math.Truncate(arg);
                    case "SIN":   return Math.Sin(arg);
                    case "COS":   return Math.Cos(arg);
                    case "TAN":   return Math.Tan(arg);
                    case "ASIN":  return Math.Asin(arg);
                    case "ACOS":  return Math.Acos(arg);
                    case "ATAN":  return Math.Atan(arg);
                    case "EXP":   return Math.Exp(arg);
                    case "LN":    return Math.Log(arg);
                    case "LG":
                    case "LOG":   return Math.Log10(arg);
                    default: throw new InvalidOperationException($"Unknown GenICam function '{name}'");
                }
            }

            private void Skip()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            private static bool IsHexDigit(char c)
                => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }
    }
}
