using System;
using System.Collections.Generic;
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
        private readonly Dictionary<ulong, string> _chunkIdToName = new Dictionary<ulong, string>();
        private readonly Dictionary<string, ulong> _portToChunkId = new Dictionary<string, ulong>(StringComparer.Ordinal);

        internal NodeMap(GenTLApi api, IntPtr port)
        {
            _api = api;
            _port = port;
            var xml = GenICamXmlExtractor.FetchXml(_api, _port);
            ParseXml(xml);
        }

        // Offline constructor: parses a GenICam XML string with no live device. Only the
        // XML-derived surfaces (node graph, chunk ID map, TryReadChunk) are usable — Read/Write
        // throw because there is no port to talk to. Used by the unit-test harness to exercise
        // chunk decoding deterministically against the saved example-camera XML fixtures.
        internal NodeMap(string xml)
        {
            _api = null!;
            _port = IntPtr.Zero;
            ParseXml(xml);
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

                // BFS/FLIR-style chunk layout: <ChunkID> lives on a <Port> node; terminal
                // IntReg/FloatReg/StringReg reference it via <pPort>. Collect the mapping here
                // so BuildChunkIdMap() can walk feature → pValue chain → pPort → chunk ID.
                if (el.Name.LocalName == "Port")
                {
                    ulong portChunkId = ParseULong(el, ns, "ChunkID", 0);
                    if (portChunkId != 0) _portToChunkId[name] = portChunkId;
                    continue;
                }

                // StructReg: unroll each StructEntry into a MaskedIntRegNode so existing
                // read/write code handles them without a dedicated node type.
                if (el.Name.LocalName == "StructReg")
                {
                    ulong structAddr = ParseAddress(el, ns);
                    string[]? structPAddrs = ParsePAddresses(el, ns);
                    int structLen = ParseInt(el, ns, "Length", 4);
                    bool structLE = !string.Equals((string)el.Element(ns + "Endianess"), "BigEndian", StringComparison.OrdinalIgnoreCase);
                    string? structPPort = (string)el.Element(ns + "pPort");
                    foreach (var entry in el.Elements(ns + "StructEntry"))
                    {
                        string entryName = (string)entry.Attribute("Name");
                        if (string.IsNullOrEmpty(entryName)) continue;
                        var entryAm = ParseAccessMode((string)entry.Element(ns + "AccessMode") ?? (string)entry.Attribute("AccessMode") ?? "RW");
                        bool entryUnsigned = !string.Equals((string)entry.Element(ns + "Sign"), "Signed", StringComparison.OrdinalIgnoreCase);
                        var (entryMask, entryShift) = ParseBitMaskShift(entry, ns);
                        var entryNode = new MaskedIntRegNode
                        {
                            Name = entryName, AccessMode = entryAm,
                            Address = structAddr, PAddresses = structPAddrs,
                            Length = structLen, LittleEndian = structLE,
                            Unsigned = entryUnsigned, Mask = entryMask, Shift = entryShift,
                            PPort = structPPort
                        };
                        entryNode.Description    = ((string)entry.Element(ns + "Description"))?.Trim();
                        entryNode.ToolTip        = ((string)entry.Element(ns + "ToolTip"))?.Trim();
                        if (entryNode.Description == null) entryNode.Description = entryNode.ToolTip;
                        entryNode.Visibility     = ParseVisibility((string)entry.Element(ns + "Visibility"));
                        entryNode.PIsImplemented = ((string)entry.Element(ns + "pIsImplemented"))?.Trim();
                        entryNode.PIsAvailable   = ((string)entry.Element(ns + "pIsAvailable"))?.Trim();
                        entryNode.PIsLocked      = ((string)entry.Element(ns + "pIsLocked"))?.Trim();
                        _nodes[entryName] = entryNode;
                    }
                    continue;
                }

                NodeBase? node = ParseElement(el, ns, name);
                if (node != null)
                {
                    node.Description    = ((string)el.Element(ns + "Description"))?.Trim();
                    node.ToolTip        = ((string)el.Element(ns + "ToolTip"))?.Trim();
                    if (node.Description == null) node.Description = node.ToolTip;
                    node.Visibility     = ParseVisibility((string)el.Element(ns + "Visibility"));
                    node.PIsImplemented = ((string)el.Element(ns + "pIsImplemented"))?.Trim();
                    node.PIsAvailable   = ((string)el.Element(ns + "pIsAvailable"))?.Trim();
                    node.PIsLocked      = ((string)el.Element(ns + "pIsLocked"))?.Trim();
                    node.PSelected      = ParsePSelected(el, ns);
                    _nodes[name] = node;
                }
            }

            // Port-based chunk layout (e.g. BFS-U3): walk feature → pValue/pVariable chains
            // to find a terminal register with pPort referencing a chunk Port, then map chunk
            // ID → logical feature name. Pass 2 overrides register-level names with logical names.
            BuildChunkIdMap();
        }

        private static string[]? ParsePAddresses(XElement el, XNamespace ns)
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var pa in el.Elements(ns + "pAddress"))
                if (!string.IsNullOrEmpty(pa.Value)) list.Add(pa.Value.Trim());
            return list.Count > 0 ? list.ToArray() : null;
        }

        // <pSelected> children name the features this (selector) node governs. Writing the
        // selector changes the camera's internal pointer, so those features read back new values.
        private static List<string> ParsePSelected(XElement el, XNamespace ns)
        {
            var list = new List<string>();
            foreach (var ps in el.Elements(ns + "pSelected"))
                if (!string.IsNullOrEmpty(ps.Value)) list.Add(ps.Value.Trim());
            return list;
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
                        LittleEndian = !string.Equals((string)el.Element(ns + "Endianess"), "BigEndian", StringComparison.OrdinalIgnoreCase),
                        PPort = (string)el.Element(ns + "pPort")
                    };

                case "MaskedIntReg":
                {
                    var (mask, shift) = ParseBitMaskShift(el, ns);
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
                        LittleEndian = !string.Equals((string)el.Element(ns + "Endianess"), "BigEndian", StringComparison.OrdinalIgnoreCase),
                        PPort = (string)el.Element(ns + "pPort")
                    };
                }

                case "FloatReg":
                    return new FloatRegNode
                    {
                        Name = name,
                        AccessMode = accessMode,
                        Address = ParseAddress(el, ns),
                        PAddresses = ParsePAddresses(el, ns),
                        Length = ParseInt(el, ns, "Length", 4),
                        PPort = (string)el.Element(ns + "pPort")
                    };

                case "StringReg":
                    return new StringRegNode
                    {
                        Name = name,
                        AccessMode = accessMode,
                        Address = ParseAddress(el, ns),
                        PAddresses = ParsePAddresses(el, ns),
                        Length = ParseInt(el, ns, "Length", 64),
                        PLength = (string)el.Element(ns + "pLength"),
                        PPort = (string)el.Element(ns + "pPort")
                    };

                case "Integer":
                {
                    string pv = (string)el.Element(ns + "pValue");
                    string val = (string)el.Element(ns + "Value");
                    if (pv == null && val != null)
                        return new IntegerNode { Name = name, AccessMode = NodeAccessMode.RO, ConstantValue = ParseLongLiteral(val) };
                    string minS = (string)el.Element(ns + "Min"), maxS = (string)el.Element(ns + "Max"), incS = (string)el.Element(ns + "Inc");
                    string dpS = (string)el.Element(ns + "DisplayPrecision");
                    return new IntegerNode
                    {
                        Name = name, AccessMode = accessMode, PValue = pv,
                        LiteralMin = minS != null ? (long?)ParseLongLiteral(minS) : null,
                        LiteralMax = maxS != null ? (long?)ParseLongLiteral(maxS) : null,
                        LiteralInc = incS != null ? (long?)ParseLongLiteral(incS) : null,
                        PMin = (string)el.Element(ns + "pMin"),
                        PMax = (string)el.Element(ns + "pMax"),
                        PInc = (string)el.Element(ns + "pInc"),
                        Unit = ((string)el.Element(ns + "Unit"))?.Trim(),
                        Representation = ParseRepresentation((string)el.Element(ns + "Representation")),
                        DisplayNotation = ParseDisplayNotation((string)el.Element(ns + "DisplayNotation")),
                        DisplayPrecision = dpS != null ? (int?)int.Parse(dpS.Trim()) : null
                    };
                }

                case "Float":
                {
                    string minS = (string)el.Element(ns + "Min"), maxS = (string)el.Element(ns + "Max");
                    string incS = (string)el.Element(ns + "Inc");
                    string dpS = (string)el.Element(ns + "DisplayPrecision");
                    return new FloatNode
                    {
                        Name = name, AccessMode = accessMode,
                        PValue = (string)el.Element(ns + "pValue"),
                        LiteralMin = minS != null ? (double?)double.Parse(minS.Trim(), System.Globalization.CultureInfo.InvariantCulture) : null,
                        LiteralMax = maxS != null ? (double?)double.Parse(maxS.Trim(), System.Globalization.CultureInfo.InvariantCulture) : null,
                        LiteralInc = incS != null ? (double?)double.Parse(incS.Trim(), System.Globalization.CultureInfo.InvariantCulture) : null,
                        PMin = (string)el.Element(ns + "pMin"),
                        PMax = (string)el.Element(ns + "pMax"),
                        PInc = (string)el.Element(ns + "pInc"),
                        Unit = ((string)el.Element(ns + "Unit"))?.Trim(),
                        Representation = ParseRepresentation((string)el.Element(ns + "Representation")),
                        DisplayNotation = ParseDisplayNotation((string)el.Element(ns + "DisplayNotation")),
                        DisplayPrecision = dpS != null ? (int?)int.Parse(dpS.Trim()) : null
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
                    var entryGuards = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
                    foreach (var entry in el.Elements(ns + "EnumEntry"))
                    {
                        string entryName = (string)entry.Attribute("Name");
                        long value = ParseLong(entry, ns, "Value", 0);
                        if (entryName != null)
                        {
                            entries[entryName] = value;
                            byValue[value] = entryName;
                            string? pImpl  = ((string)entry.Element(ns + "pIsImplemented"))?.Trim();
                            string? pAvail = ((string)entry.Element(ns + "pIsAvailable"))?.Trim();
                            if (pImpl != null || pAvail != null)
                                entryGuards[entryName] = (pImpl, pAvail);
                        }
                    }
                    // Some cameras (e.g. FLIR TriggerSelector/BinningSelector) express the current
                    // value as a node-level <Value> instead of a <pValue> register reference.
                    // el.Element returns only the direct-child <Value>, never the nested EnumEntry ones.
                    string enumPValue = (string)el.Element(ns + "pValue");
                    string enumVal = (string)el.Element(ns + "Value");
                    return new EnumerationNode
                    {
                        Name = name,
                        AccessMode = accessMode,
                        PValue = enumPValue,
                        ConstantValue = (enumPValue == null && enumVal != null) ? ParseLongLiteral(enumVal) : null,
                        Entries = entries,
                        SymbolicByValue = byValue,
                        EntryGuards = entryGuards
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
                {
                    var vars = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var v in el.Elements(ns + "pVariable"))
                    { string vn = (string)v.Attribute("Name"); if (vn != null) vars[vn] = v.Value.Trim(); }
                    return new ConverterNode
                    {
                        Name = name, AccessMode = accessMode,
                        PValue = (string)el.Element(ns + "pValue"),
                        FormulaTo = (string)el.Element(ns + "FormulaTo") ?? "FROM",
                        FormulaFrom = (string)el.Element(ns + "FormulaFrom") ?? "TO",
                        Variables = vars
                    };
                }

                case "IntConverter":
                {
                    var vars = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var v in el.Elements(ns + "pVariable"))
                    { string vn = (string)v.Attribute("Name"); if (vn != null) vars[vn] = v.Value.Trim(); }
                    return new IntConverterNode
                    {
                        Name = name, AccessMode = accessMode,
                        PValue = (string)el.Element(ns + "pValue"),
                        FormulaTo = (string)el.Element(ns + "FormulaTo") ?? "FROM",
                        FormulaFrom = (string)el.Element(ns + "FormulaFrom") ?? "TO",
                        Variables = vars
                    };
                }

                default:
                    return null;
            }
        }

        // ---- public read/write API ----

        internal FeatureValue Read(string name)
        {
            var node = Resolve(name);
            if (node.AccessMode == NodeAccessMode.NI)
                throw new InvalidOperationException($"Feature '{name}' is not implemented on this device.");
            return new FeatureValue(name, ReadNode(node));
        }

        internal void Write(string name, string valueStr)
        {
            var node = Resolve(name);
            WriteNode(node, valueStr);
        }

        internal bool CanWrite(string name)
        {
            if (!_nodes.TryGetValue(name, out var node)) return false;
            if (!IsNodeAvailable(node)) return false;
            return EffectiveWritable(node);
        }

        // Returns false when the hardware reports a feature as unavailable via
        // pIsImplemented/pIsAvailable guards (e.g. IDS cameras block ExposureAuto,
        // GainAuto, and BalanceWhiteAuto at the GCWritePort level when the
        // AutofeatureAvailableReg bitmask is 0). Features remain visible in the editor
        // as read-only rather than disappearing.
        private bool IsNodeAvailable(NodeBase node)
            => GuardPasses(node.PIsImplemented) && GuardPasses(node.PIsAvailable);

        // A pIsImplemented / pIsAvailable guard passes when it is absent, or resolves to a non-zero
        // value. A reference that resolves to 0 — or fails to resolve — fails the guard.
        private bool GuardPasses(string? pRef)
        {
            if (pRef == null) return true;
            try { return Convert.ToInt64(ReadNode(Resolve(pRef))) != 0; }
            catch { return false; }
        }

        // Traverses the pValue chain to the terminal register node so that a logical
        // node with no <AccessMode> (which defaults to RW) does not appear writable
        // when its backing register declares RO — e.g. DeviceTemperature.
        private bool EffectiveWritable(NodeBase node)
        {
            if (node.AccessMode == NodeAccessMode.RO || node.AccessMode == NodeAccessMode.NA || node.AccessMode == NodeAccessMode.NI)
                return false;
            if (node.PIsLocked != null)
                try { if (Convert.ToInt64(ReadNode(Resolve(node.PIsLocked))) != 0) return false; }
                catch { }
            // Terminal register nodes: their AccessMode is authoritative
            if (node is IntRegNode || node is FloatRegNode || node is StringRegNode || node is MaskedIntRegNode)
                return node.AccessMode == NodeAccessMode.RW || node.AccessMode == NodeAccessMode.WO;
            // Formula-only nodes are always read-only
            if (node is IntSwissKnifeNode || node is SwissKnifeNode)
                return false;
            // Inline-<Value> enumerations (no <pValue>): a selector (has <pSelected>) is writable —
            // the write updates a client-side value that drives its governed features' addressing.
            // A value-less enum that selects nothing has no register and no effect, so keep it
            // read-only. (AccessMode RO/NA/NI already returned false above.)
            if (node is EnumerationNode en && en.PValue == null)
                return en.PSelected.Count > 0;
            // Chain nodes: follow pValue to the terminal
            string? pv = NodePValue(node);
            if (pv != null)
            {
                try { return EffectiveWritable(Resolve(pv)); }
                catch { return false; }
            }
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

        // <pSelected> targets of a selector node: the features whose values change as a
        // side effect of writing this node. Empty for non-selector nodes.
        internal IReadOnlyList<string> GetSelectedFeatures(string name)
            => _nodes.TryGetValue(name, out var node) ? node.PSelected : Array.Empty<string>();

        // True when the node is an enumeration whose value is held inline (<Value>, no <pValue>).
        // These are written client-side (WriteNode updates the stored value) rather than to a register.
        internal bool IsInlineValueEnum(string name)
            => _nodes.TryGetValue(name, out var node) && node is EnumerationNode e && e.PValue == null && e.ConstantValue.HasValue;

        internal NodeVisibility GetNodeVisibility(string name)
            => _nodes.TryGetValue(name, out var node) ? node.Visibility : NodeVisibility.Beginner;

        internal string? GetNodeUnit(string name)
        {
            if (!_nodes.TryGetValue(name, out var node)) return null;
            return FindUnit(node);
        }

        internal NodeRepresentation GetNodeRepresentation(string name)
        {
            if (!_nodes.TryGetValue(name, out var node)) return NodeRepresentation.Linear;
            return FindRepresentation(node) ?? NodeRepresentation.Linear;
        }

        internal int? GetNodeDisplayPrecision(string name)
        {
            if (!_nodes.TryGetValue(name, out var node)) return null;
            return FindDisplayPrecision(node);
        }

        internal NodeDisplayNotation GetNodeDisplayNotation(string name)
        {
            if (!_nodes.TryGetValue(name, out var node)) return NodeDisplayNotation.Automatic;
            return FindDisplayNotation(node);
        }

        private string? FindUnit(NodeBase node)
        {
            string? unit = node is IntegerNode i ? i.Unit : node is FloatNode f ? f.Unit : null;
            if (unit != null) return unit;
            string? pv = NodePValue(node);
            if (pv != null) { try { return FindUnit(Resolve(pv)); } catch { } }
            return null;
        }

        private NodeRepresentation? FindRepresentation(NodeBase node)
        {
            if (node is IntegerNode i) return i.Representation;
            if (node is FloatNode f)   return f.Representation;
            string? pv = NodePValue(node);
            if (pv != null) { try { return FindRepresentation(Resolve(pv)); } catch { } }
            return null;
        }

        private int? FindDisplayPrecision(NodeBase node)
        {
            if (node is IntegerNode i) return i.DisplayPrecision;
            if (node is FloatNode f)   return f.DisplayPrecision;
            string? pv = NodePValue(node);
            if (pv != null) { try { return FindDisplayPrecision(Resolve(pv)); } catch { } }
            return null;
        }

        private NodeDisplayNotation FindDisplayNotation(NodeBase node)
        {
            if (node is IntegerNode i) return i.DisplayNotation;
            if (node is FloatNode f)   return f.DisplayNotation;
            string? pv = NodePValue(node);
            if (pv != null) { try { return FindDisplayNotation(Resolve(pv)); } catch { } }
            return NodeDisplayNotation.Automatic;
        }

        internal FeatureKind GetNodeKind(string name)
        {
            if (!_nodes.TryGetValue(name, out var node)) return FeatureKind.Text;
            return EffectiveKind(node);
        }

        // Walks the full pValue chain to the terminal register node and classifies based on that.
        // FloatReg/SwissKnife → Float; IntReg/MaskedIntReg/IntSwissKnife → Integer.
        // All intermediate chain nodes (FloatNode, IntegerNode, ConverterNode, IntConverterNode)
        // are transparent — so ExposureTime → [any converter] → IntReg correctly returns Integer.
        private FeatureKind EffectiveKind(NodeBase node)
        {
            // Terminal register nodes — classification ends here
            if (node is FloatRegNode || node is SwissKnifeNode) return FeatureKind.Float;
            if (node is IntRegNode || node is MaskedIntRegNode || node is IntSwissKnifeNode) return FeatureKind.Integer;
            if (node is StringRegNode || node is StringNode) return FeatureKind.Text;
            if (node is EnumerationNode) return FeatureKind.Enumeration;
            if (node is BooleanNode)     return FeatureKind.Boolean;
            if (node is CommandNode)     return FeatureKind.Command;

            // Chain nodes — follow pValue to the terminal
            string? pv = NodePValue(node);
            if (pv != null)
            {
                try { return EffectiveKind(Resolve(pv)); }
                catch { }
            }
            // Fallback for unresolvable chains
            return node is FloatNode || node is ConverterNode ? FeatureKind.Float : FeatureKind.Integer;
        }

        private static string? NodePValue(NodeBase node) => node switch
        {
            FloatNode n        => n.PValue,
            IntegerNode n      => n.ConstantValue.HasValue ? null : n.PValue,
            ConverterNodeBase n => n.PValue,
            EnumerationNode n  => n.PValue,
            BooleanNode n      => n.PValue,
            StringNode n       => n.PValue,
            _                  => null
        };

        internal IReadOnlyList<string> GetEnumEntries(string name)
        {
            if (_nodes.TryGetValue(name, out var node) && node is EnumerationNode en)
            {
                var list = new List<string>(en.Entries.Count);
                foreach (string key in en.Entries.Keys)
                    if (IsEnumEntryAvailable(en, key)) list.Add(key);
                return list;
            }
            return new string[0];
        }

        // All declared enum entry names, ignoring pIsAvailable/pIsImplemented guards.
        // GetEnumEntries filters by those guards (for UI), but some producers report a chunk
        // selector entry as "unavailable" while still accepting a write to it — callers that
        // want to attempt every entry and handle rejection themselves use this.
        internal IReadOnlyList<string> GetAllEnumEntries(string name)
        {
            if (_nodes.TryGetValue(name, out var node) && node is EnumerationNode en)
                return new List<string>(en.Entries.Keys);
            return new string[0];
        }

        private bool IsEnumEntryAvailable(EnumerationNode en, string entryName)
        {
            if (!en.EntryGuards.TryGetValue(entryName, out var guards)) return true;
            return GuardPasses(guards.PIsImplemented) && GuardPasses(guards.PIsAvailable);
        }

        internal IEnumerable<string> GetCommandNodeNames()
        {
            foreach (var kv in _nodes)
                if (kv.Value is CommandNode) yield return kv.Key;
        }

        internal (double? min, double? max, double? step) GetNodeLimits(string name)
        {
            if (!_nodes.TryGetValue(name, out var node)) return (null, null, null);
            return EffectiveLimits(node);
        }

        private (double? min, double? max, double? step) EffectiveLimits(NodeBase node)
        {
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
                // Read pMin/pMax/pInc directly if declared on the Float node — they are already in user units.
                //
                // FLIR (Blackfly S): Float.pMin/pMax → SwissKnife (Formula: RAW, trivial passthrough) → IntReg.
                //   The backing Converter is also trivial (FormulaTo: FROM). Limits arrive here in µs.
                //
                // IDS (UI322xCP-M): Float.pMin/pMax → SwissKnife (Formula: MS/1000) → IntReg.
                //   The SwissKnife converts the raw register to µs independently of the Converter.
                //   Even when the Converter's FormulaTo/FormulaFrom are inverted on some IDS firmware
                //   versions (requiring the swap detected in ReadNode/WriteNode), the SwissKnife still
                //   produces correct µs limits — so limits are always right here regardless.
                double? min  = f.LiteralMin.HasValue ? (double?)f.LiteralMin.Value : (f.PMin != null ? TryReadRef(f.PMin) : null);
                double? max  = f.LiteralMax.HasValue ? (double?)f.LiteralMax.Value : (f.PMax != null ? TryReadRef(f.PMax) : null);
                double? step = f.LiteralInc.HasValue ? (double?)f.LiteralInc.Value : (f.PInc != null ? TryReadRef(f.PInc) : null);
                if (f.PValue != null)
                {
                    try
                    {
                        var next = Resolve(f.PValue);
                        if (next is ConverterNodeBase)
                        {
                            // HikRobot (MV-CA013): Float declares no pMin/pMax. Limits live on the
                            // Integer node that backs the Converter, in raw register units (ADC counts,
                            // raw ticks, etc.). Apply FormulaTo with resolved pVariables to convert
                            // them to user units (µs, dB, …).
                            // FLIR/IDS skip this block because their pMin/pMax are already set above.
                            if (!min.HasValue || !max.HasValue)
                            {
                                var cv = (ConverterNodeBase)next;
                                string? cvPValue = cv.PValue;
                                string cvFTo = cv.FormulaTo ?? "FROM";
                                Dictionary<string, string> cvVars = cv.Variables;
                                if (cvPValue != null)
                                {
                                    try
                                    {
                                        var (rawMin, rawMax, _) = EffectiveLimits(Resolve(cvPValue));
                                        if (!min.HasValue && rawMin.HasValue)
                                        {
                                            var fv = ResolveConverterVars(cvVars); fv["FROM"] = rawMin.Value;
                                            try { min = EvaluateFormula(cvFTo, fv); } catch { }
                                        }
                                        if (!max.HasValue && rawMax.HasValue)
                                        {
                                            var fv = ResolveConverterVars(cvVars); fv["FROM"] = rawMax.Value;
                                            try { max = EvaluateFormula(cvFTo, fv); } catch { }
                                        }
                                        // FormulaTo may be monotonically decreasing — ensure min <= max.
                                        if (min.HasValue && max.HasValue && min.Value > max.Value)
                                        { (min, max) = (max, min); }
                                    }
                                    catch { }
                                }
                            }
                        }
                        else
                        {
                            // No unit-conversion layer — recurse directly to find limits on the backing node.
                            var (bMin, bMax, bStep) = EffectiveLimits(next);
                            if (!min.HasValue) min = bMin;
                            if (!max.HasValue) max = bMax;
                            return (min, max, step ?? bStep);
                        }
                    }
                    catch { }
                }
                return (min, max, step);
            }
            // Converter nodes change unit space — limits below them are meaningless in user units.
            if (node is ConverterNodeBase) return (null, null, null);
            string? pv = NodePValue(node);
            if (pv != null)
            {
                try { return EffectiveLimits(Resolve(pv)); }
                catch { }
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

        // Maps chunk ID values (from <ChunkID> XML elements) to feature names.
        internal IReadOnlyDictionary<ulong, string> ChunkIdToName => _chunkIdToName;

        // Parses chunk bytes for a named feature using the NodeMap's node graph, applying
        // the same Converter/IntConverter formula chain as a normal register read.
        // Returns null if the feature is not found or parsing fails.
        internal object? TryReadChunk(string featureName, byte[] bytes)
        {
            if (!_nodes.TryGetValue(featureName, out var node)) return null;
            try { return ReadNodeFromChunk(node, bytes); }
            catch { return null; }
        }

        private object ReadNodeFromChunk(NodeBase node, byte[] bytes)
        {
            switch (node)
            {
                case MaskedIntRegNode r:
                {
                    ulong raw = ToUInt64(bytes, Math.Min(r.Length, bytes.Length), r.LittleEndian);
                    long fullVal = r.Unsigned ? (long)raw : SignExtend(raw, r.Length);
                    return (long)(((ulong)fullVal & r.Mask) >> r.Shift);
                }
                case IntRegNode r:
                {
                    ulong raw = ToUInt64(bytes, Math.Min(r.Length, bytes.Length), r.LittleEndian);
                    return r.Unsigned ? (long)raw : SignExtend(raw, r.Length);
                }
                case FloatRegNode r:
                {
                    byte[] buf = r.LittleEndian ? bytes : Reverse(bytes);
                    return r.Length == 4
                        ? (object)(double)BitConverter.ToSingle(buf, 0)
                        : BitConverter.ToDouble(buf, 0);
                }
                case StringRegNode r:
                {
                    int len = Array.IndexOf(bytes, (byte)0);
                    return Encoding.ASCII.GetString(bytes, 0, len < 0 ? Math.Min(bytes.Length, r.Length) : len);
                }
                case IntegerNode n:
                    if (n.ConstantValue.HasValue) return n.ConstantValue.Value;
                    return ReadNodeFromChunk(ResolveRef(n.PValue), bytes);
                case FloatNode n:
                    return ReadNodeFromChunk(ResolveRef(n.PValue), bytes);
                case BooleanNode n:
                    return Convert.ToInt64(ReadNodeFromChunk(ResolveRef(n.PValue), bytes)) != 0;
                case EnumerationNode n:
                {
                    long val = Convert.ToInt64(ReadNodeFromChunk(ResolveRef(n.PValue), bytes));
                    return n.SymbolicByValue.TryGetValue(val, out string sym) ? (object)sym : val;
                }
                case SwissKnifeNode n:
                {
                    var vars = ResolveFormulaVarsFromChunk(n.Variables, bytes);
                    return EvaluateFormula(n.Formula, vars);
                }
                case IntSwissKnifeNode n:
                {
                    var vars = ResolveFormulaVarsFromChunk(n.Variables, bytes);
                    return (long)EvaluateFormula(n.Formula, vars);
                }
                case ConverterNodeBase n:
                {
                    double from = Convert.ToDouble(ReadNodeFromChunk(ResolveRef(n.PValue), bytes));
                    var vars = ResolveConverterVars(n.Variables);
                    vars["FROM"] = from;
                    double result = EvaluateFormula(n.FormulaTo, vars);
                    return n is IntConverterNode ? (object)(long)result : (object)result;
                }
                default:
                    throw new NotSupportedException($"ReadChunk: unsupported node type {node.GetType().Name}");
            }
        }

        // ---- chunk ID map (Port-based layout) ----

        // Two-pass build: pass 1 seeds any reachable node name; pass 2 overwrites with the
        // logical feature name (Integer/Float/Enumeration/Boolean/String) when one exists,
        // so callers get "ChunkExposureTime" rather than "ChunkExposureTime_Val".
        private void BuildChunkIdMap()
        {
            foreach (var kv in _nodes)
            {
                ulong? id;
                try { id = FindChunkId(kv.Value); }
                catch { continue; }
                if (id.HasValue) _chunkIdToName[id.Value] = kv.Key;
            }
            foreach (var kv in _nodes)
            {
                if (!(kv.Value is IntegerNode || kv.Value is FloatNode || kv.Value is EnumerationNode ||
                      kv.Value is BooleanNode || kv.Value is StringNode)) continue;
                ulong? id;
                try { id = FindChunkId(kv.Value); }
                catch { continue; }
                if (id.HasValue) _chunkIdToName[id.Value] = kv.Key;
            }
        }

        // Walks the node graph to find the Port whose <ChunkID> identifies this node's chunk.
        // Follows pValue chains, then SwissKnife pVariable refs when no pValue chain leads there.
        private ulong? FindChunkId(NodeBase node, int depth = 0)
        {
            if (depth > 20) return null;

            string? pPort = GetNodePPort(node);
            if (pPort != null && _portToChunkId.TryGetValue(pPort, out ulong chunkId))
                return chunkId;

            string? pv = NodePValue(node);
            if (pv != null && _nodes.TryGetValue(pv, out var pValueNode))
            {
                ulong? id = FindChunkId(pValueNode, depth + 1);
                if (id.HasValue) return id;
            }

            Dictionary<string, string>? vars = node is SwissKnifeNode sk ? sk.Variables :
                                               node is IntSwissKnifeNode isk ? isk.Variables : null;
            if (vars != null)
            {
                foreach (var varRef in vars.Values)
                {
                    if (_nodes.TryGetValue(varRef, out var varNode))
                    {
                        ulong? id = FindChunkId(varNode, depth + 1);
                        if (id.HasValue) return id;
                    }
                }
            }

            return null;
        }

        private static string? GetNodePPort(NodeBase node) => node is IRegisterNode reg ? reg.PPort : null;

        // GenICam allows <Bit>n</Bit> as shorthand for <Mask>1<<n</Mask> + <Shift>n</Shift>;
        // otherwise read explicit <Mask>/<Shift>. Shared by the MaskedIntReg and StructEntry parsers.
        private static (ulong mask, int shift) ParseBitMaskShift(XElement el, XNamespace ns)
        {
            string? bitStr = (string)el.Element(ns + "Bit");
            if (bitStr != null)
            {
                string bt = bitStr.Trim();
                int bit = bt.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? (int)Convert.ToUInt32(bt, 16) : int.Parse(bt);
                return (1UL << bit, bit);
            }
            return (ParseULong(el, ns, "Mask", ulong.MaxValue), ParseInt(el, ns, "Shift", 0));
        }

        // Like ResolveFormulaVars but reads from chunk bytes first, falling back to the
        // live register when a variable is not part of the chunk (e.g. a calibration constant).
        private Dictionary<string, double> ResolveFormulaVarsFromChunk(Dictionary<string, string> variables, byte[] bytes)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var kv in variables)
            {
                if (!_nodes.TryGetValue(kv.Value, out var refNode)) continue;
                double val;
                try { val = Convert.ToDouble(ReadNodeFromChunk(refNode, bytes)); }
                catch { try { val = Convert.ToDouble(ReadNode(refNode)); } catch { continue; } }
                result[kv.Key] = val;
            }
            return result;
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
                case FloatNode n:
                {
                    var inner = ResolveRef(n.PValue);
                    // Detect inverted Converter formulas: some cameras (e.g. IDS) accidentally swap
                    // FormulaTo/FormulaFrom. Detect by checking if the FormulaTo result lands way
                    // outside the node's declared pMin/pMax limits, then try FormulaFrom instead.
                    if (inner is ConverterNodeBase cv && (n.PMin != null || n.PMax != null))
                    {
                        string? cvPValue = cv.PValue; string cvFTo = cv.FormulaTo ?? "FROM"; string cvFFrom = cv.FormulaFrom ?? "TO"; Dictionary<string, string> cvVars = cv.Variables;
                        double raw = Convert.ToDouble(ReadNode(ResolveRef(cvPValue)));
                        var fvars = ResolveConverterVars(cvVars);
                        fvars["FROM"] = raw;
                        double normalResult = EvaluateFormula(cvFTo, fvars);
                        double? limMin = null, limMax = null;
                        if (n.PMin != null) try { limMin = Convert.ToDouble(ReadNode(Resolve(n.PMin))); } catch { }
                        if (n.PMax != null) try { limMax = Convert.ToDouble(ReadNode(Resolve(n.PMax))); } catch { }
                        bool outsideLimits = (limMax.HasValue && limMax.Value > 0 && normalResult > limMax.Value * 100) ||
                                             (limMin.HasValue && limMin.Value > 0 && normalResult < limMin.Value / 100);
                        if (outsideLimits)
                        {
                            var fvars2 = ResolveConverterVars(cvVars);
                            fvars2["TO"] = raw;
                            try
                            {
                                double inv = EvaluateFormula(cvFFrom, fvars2);
                                bool inRange = (!limMin.HasValue || inv >= limMin.Value * 0.99) &&
                                               (!limMax.HasValue || inv <= limMax.Value * 1.01);
                                if (inRange) return inv;
                            }
                            catch { }
                        }
                        return normalResult;
                    }
                    return ReadNode(inner);
                }
                case StringNode n: return ReadNode(ResolveRef(n.PValue));
                case BooleanNode n: return Convert.ToInt64(ReadNode(ResolveRef(n.PValue))) != 0;
                case EnumerationNode n:
                {
                    long val = n.PValue == null && n.ConstantValue.HasValue
                        ? n.ConstantValue.Value
                        : Convert.ToInt64(ReadNode(ResolveRef(n.PValue)));
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
                case ConverterNodeBase n:
                {
                    double from = Convert.ToDouble(ReadNode(ResolveRef(n.PValue)));
                    var vars = ResolveConverterVars(n.Variables);
                    vars["FROM"] = from;
                    double result = EvaluateFormula(n.FormulaTo, vars);
                    return n is IntConverterNode ? (object)(long)result : (object)result;
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
                    WriteMaskedIntReg(r, (long)double.Parse(valueStr, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case IntRegNode r:
                    WriteIntReg(r, (long)double.Parse(valueStr, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case FloatRegNode r:
                    WriteFloatReg(r, double.Parse(valueStr, System.Globalization.CultureInfo.InvariantCulture));
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
                {
                    var inner = ResolveRef(n.PValue);
                    if (inner is ConverterNodeBase cv && (n.PMin != null || n.PMax != null))
                    {
                        string? cvPValue = cv.PValue; string cvFTo = cv.FormulaTo ?? "FROM"; string cvFFrom = cv.FormulaFrom ?? "TO"; Dictionary<string, string> cvVars = cv.Variables;
                        double? limMin = null, limMax = null;
                        if (n.PMin != null) try { limMin = Convert.ToDouble(ReadNode(Resolve(n.PMin))); } catch { }
                        if (n.PMax != null) try { limMax = Convert.ToDouble(ReadNode(Resolve(n.PMax))); } catch { }
                        bool isInverted = false;
                        if (limMin.HasValue || limMax.HasValue)
                        {
                            double curRaw = Convert.ToDouble(ReadNode(ResolveRef(cvPValue)));
                            var rvars = ResolveConverterVars(cvVars);
                            rvars["FROM"] = curRaw;
                            double curNormal = EvaluateFormula(cvFTo, rvars);
                            isInverted = (limMax.HasValue && limMax.Value > 0 && curNormal > limMax.Value * 100) ||
                                         (limMin.HasValue && limMin.Value > 0 && curNormal < limMin.Value / 100);
                        }
                        double userVal = double.Parse(valueStr, System.Globalization.CultureInfo.InvariantCulture);
                        var wvars = ResolveConverterVars(cvVars);
                        double rawToWrite;
                        if (isInverted)
                        { wvars["FROM"] = userVal; rawToWrite = EvaluateFormula(cvFTo, wvars); }
                        else
                        { wvars["TO"] = userVal; rawToWrite = EvaluateFormula(cvFFrom, wvars); }
                        bool isIntConverter = inner is IntConverterNode;
                        string rawStr = isIntConverter
                            ? ((long)rawToWrite).ToString()
                            : rawToWrite.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        WriteNode(ResolveRef(cvPValue), rawStr);
                        break;
                    }
                    WriteNode(inner, valueStr);
                    break;
                }
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
                    if (n.PValue == null && n.ConstantValue.HasValue)
                    {
                        // Inline-<Value> selector: no register to write. Update the client-side
                        // value, which drives the pAddress/SwissKnife addressing of its pSelected
                        // features (so their subsequent reads/writes hit the selected element).
                        n.ConstantValue = n.Entries.TryGetValue(valueStr, out long sv)
                            ? sv
                            : (long)double.Parse(valueStr, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (n.Entries.TryGetValue(valueStr, out long enumVal))
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
                    double to = double.Parse(valueStr, System.Globalization.CultureInfo.InvariantCulture);
                    var vars = ResolveConverterVars(n.Variables);
                    vars["TO"] = to;
                    double raw = EvaluateFormula(n.FormulaFrom, vars);
                    WriteNode(ResolveRef(n.PValue), raw.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                }
                case IntConverterNode n:
                {
                    double to = double.Parse(valueStr, System.Globalization.CultureInfo.InvariantCulture);
                    var vars = ResolveConverterVars(n.Variables);
                    vars["TO"] = to;
                    long raw = (long)EvaluateFormula(n.FormulaFrom, vars);
                    WriteNode(ResolveRef(n.PValue), raw.ToString());
                    break;
                }
                default:
                    throw new NotSupportedException($"Unsupported node type: {node.GetType().Name}");
            }
        }

        // ---- register read/write ----

        private ulong ResolveAddress(IRegisterNode r)
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
            ulong address = ResolveAddress(r);
            var buf = ReadPort(address, r.Length);
            return r.Length == 4
                ? BitConverter.ToSingle(r.LittleEndian ? buf : Reverse(buf), 0)
                : BitConverter.ToDouble(r.LittleEndian ? buf : Reverse(buf), 0);
        }

        private string ReadStringReg(StringRegNode r)
        {
            ulong address = ResolveAddress(r);
            int length = r.PLength != null ? (int)Convert.ToInt64(ReadNode(Resolve(r.PLength))) : r.Length;
            var buf = ReadPort(address, length);
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
            ulong address = ResolveAddress(r);
            byte[] buf = r.Length == 4
                ? BitConverter.GetBytes((float)value)
                : BitConverter.GetBytes(value);
            if (!r.LittleEndian) Array.Reverse(buf);
            WritePort(address, buf);
        }

        private void WriteStringReg(StringRegNode r, string value)
        {
            ulong address = ResolveAddress(r);
            int length = r.PLength != null ? (int)Convert.ToInt64(ReadNode(Resolve(r.PLength))) : r.Length;
            var bytes = new byte[length];
            var encoded = Encoding.ASCII.GetBytes(value);
            int copy = Math.Min(encoded.Length, length - 1);
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
            return ParseHexOrDec64(val.Trim());
        }

        private static long ParseLongLiteral(string s)
        {
            s = s.Trim();
            return (long)ParseHexOrDec64(s);
        }

        private static int ParseInt(XElement el, XNamespace ns, string localName, int defaultValue)
        {
            string val = (string)el.Element(ns + localName);
            if (val == null) return defaultValue;
            val = val.Trim();
            return val.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? (int)Convert.ToUInt32(val, 16)
                : int.Parse(val);
        }

        private static long ParseLong(XElement el, XNamespace ns, string localName, long defaultValue)
        {
            string val = (string)el.Element(ns + localName);
            if (val == null) return defaultValue;
            return (long)ParseHexOrDec64(val.Trim());
        }

        private static ulong ParseULong(XElement el, XNamespace ns, string localName, ulong defaultValue)
        {
            string val = (string)el.Element(ns + localName);
            if (val == null) return defaultValue;
            return ParseHexOrDec64(val.Trim());
        }

        // Parses a numeric string that may be decimal (signed or unsigned), 0x-prefixed hex,
        // or bare hex (no prefix, e.g. Flea3 ChunkID "0504000A").
        private static ulong ParseHexOrDec64(string s)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt64(s, 16);
            if (ulong.TryParse(s, out ulong dec)) return dec;
            if (long.TryParse(s, out long signed)) return (ulong)signed;  // negative decimals e.g. -1
            return Convert.ToUInt64(s, 16);  // bare hex, no prefix
        }

        private static NodeAccessMode ParseAccessMode(string s)
        {
            switch (s?.ToUpperInvariant())
            {
                case "RW": return NodeAccessMode.RW;
                case "RO": return NodeAccessMode.RO;
                case "WO": return NodeAccessMode.WO;
                case "NA": return NodeAccessMode.NA;
                case "NI": return NodeAccessMode.NI;
                default: return NodeAccessMode.RW;
            }
        }

        private static NodeVisibility ParseVisibility(string? s) => s?.Trim() switch
        {
            "Expert"    => NodeVisibility.Expert,
            "Guru"      => NodeVisibility.Guru,
            "Invisible" => NodeVisibility.Invisible,
            _           => NodeVisibility.Beginner
        };

        private static NodeRepresentation ParseRepresentation(string? s) => s?.Trim() switch
        {
            "Logarithmic" => NodeRepresentation.Logarithmic,
            "Boolean"     => NodeRepresentation.Boolean,
            "PureNumber"  => NodeRepresentation.PureNumber,
            "HexNumber"   => NodeRepresentation.HexNumber,
            "IPV4Address" => NodeRepresentation.IPV4Address,
            "MACAddress"  => NodeRepresentation.MACAddress,
            _             => NodeRepresentation.Linear
        };

        private static NodeDisplayNotation ParseDisplayNotation(string? s) => s?.Trim() switch
        {
            "Fixed"       => NodeDisplayNotation.Fixed,
            "Exponential" => NodeDisplayNotation.Exponential,
            _             => NodeDisplayNotation.Automatic
        };

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

        // Numeric value of a node for formula / address arithmetic. Enumerations yield their
        // integer value, not the symbolic string that ReadNode returns for display — otherwise a
        // selector used as a SwissKnife variable (e.g. selector-indexed register addressing on FLIR)
        // would fail to convert.
        private double ReadNodeNumeric(NodeBase node)
        {
            if (node is EnumerationNode en)
                return en.PValue == null && en.ConstantValue.HasValue
                    ? en.ConstantValue.Value
                    : Convert.ToInt64(ReadNode(ResolveRef(en.PValue)));
            return Convert.ToDouble(ReadNode(node));
        }

        // Resolves pVariable references for Converter/IntConverter nodes.
        private Dictionary<string, double> ResolveConverterVars(Dictionary<string, string> variables)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var kv in variables)
            {
                if (_nodes.TryGetValue(kv.Value, out var refNode))
                    try { result[kv.Key] = ReadNodeNumeric(refNode); } catch { }
            }
            return result;
        }

        private Dictionary<string, double> ResolveFormulaVars(Dictionary<string, string> variables)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var kv in variables)
            {
                if (_nodes.TryGetValue(kv.Value, out var refNode))
                    result[kv.Key] = ReadNodeNumeric(refNode);
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
                    // C-style ==/!= and GenApi-standard single '=' (equal) / '<>' (not equal).
                    if (_i + 1 < _s.Length && _s[_i] == '=' && _s[_i + 1] == '=')
                    { _i += 2; left = (left == ParseRelational()) ? 1 : 0; }
                    else if (_i < _s.Length && _s[_i] == '=')
                    { _i += 1; left = (left == ParseRelational()) ? 1 : 0; }
                    else if (_i + 1 < _s.Length && _s[_i] == '!' && _s[_i + 1] == '=')
                    { _i += 2; left = (left != ParseRelational()) ? 1 : 0; }
                    else if (_i + 1 < _s.Length && _s[_i] == '<' && _s[_i + 1] == '>')
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
                             (_i + 1 >= _s.Length || (_s[_i + 1] != '<' && _s[_i + 1] != '>')))
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
