using System;
using System.Collections.Generic;

namespace Bonsai.GenICam.GenApi
{
    internal enum NodeAccessMode { NI, NA, WO, RO, RW }
    internal enum NodeVisibility { Beginner, Expert, Guru, Invisible }
    internal enum NodeRepresentation { Linear, Logarithmic, Boolean, PureNumber, HexNumber, IPV4Address, MACAddress }
    internal enum NodeDisplayNotation { Automatic, Fixed, Exponential }

    internal abstract class NodeBase
    {
        public string Name { get; set; } = string.Empty;
        public NodeAccessMode AccessMode { get; set; } = NodeAccessMode.RW;
        public string? Description { get; set; }
        public string? ToolTip { get; set; }
        public NodeVisibility Visibility { get; set; } = NodeVisibility.Beginner;
        public string? PIsImplemented { get; set; }
        public string? PIsAvailable { get; set; }
        public string? PIsLocked { get; set; }
        // <pSelected> targets: features this node selects. Writing this node (a selector)
        // changes the camera's internal pointer, so the listed features read back new values.
        public List<string> PSelected { get; set; } = new List<string>();
    }

    // Register nodes hold an absolute address (plus optional <pAddress> offset refs) and an
    // optional <pPort> naming the Port node they read/write through.
    internal interface IRegisterNode
    {
        ulong Address { get; set; }
        string[]? PAddresses { get; set; }
        string? PPort { get; set; }
    }

    // Direct register nodes (hold the actual address + length)
    internal class IntRegNode : NodeBase, IRegisterNode
    {
        public ulong Address { get; set; }        // direct <Address> value (0 if absent)
        public string[]? PAddresses { get; set; }  // zero or more <pAddress> refs, all summed into Address
        public int Length { get; set; }            // bytes
        public bool Unsigned { get; set; } = true;
        public bool LittleEndian { get; set; } = true;
        public string? PPort { get; set; }         // <pPort> — names a Port node whose ChunkID identifies the chunk
    }

    internal class FloatRegNode : NodeBase, IRegisterNode
    {
        public ulong Address { get; set; }
        public string[]? PAddresses { get; set; }
        public int Length { get; set; }            // 4 = float, 8 = double
        public bool LittleEndian { get; set; } = true;
        public string? PPort { get; set; }
    }

    internal class StringRegNode : NodeBase, IRegisterNode
    {
        public ulong Address { get; set; }
        public string[]? PAddresses { get; set; }
        public int Length { get; set; }
        public string? PLength { get; set; }       // <pLength> — dynamic length from another node
        public string? PPort { get; set; }
    }

    internal class MaskedIntRegNode : IntRegNode
    {
        public ulong Mask { get; set; } = ulong.MaxValue;
        public int Shift { get; set; }
    }

    // Logical nodes (reference register nodes via pValue)
    internal class IntegerNode : NodeBase
    {
        public string? PValue { get; set; }
        public long? ConstantValue { get; set; }  // non-null when XML uses <Value> instead of <pValue>
        public long? LiteralMin { get; set; }
        public long? LiteralMax { get; set; }
        public long? LiteralInc { get; set; }
        public string? PMin { get; set; }
        public string? PMax { get; set; }
        public string? PInc { get; set; }
        public string? Unit { get; set; }
        public NodeRepresentation Representation { get; set; } = NodeRepresentation.PureNumber;
        public NodeDisplayNotation DisplayNotation { get; set; } = NodeDisplayNotation.Automatic;
        public int? DisplayPrecision { get; set; }
    }

    internal class FloatNode : NodeBase
    {
        public string? PValue { get; set; }
        public double? LiteralMin { get; set; }
        public double? LiteralMax { get; set; }
        public double? LiteralInc { get; set; }
        public string? PMin { get; set; }
        public string? PMax { get; set; }
        public string? PInc { get; set; }
        public string? Unit { get; set; }
        public NodeRepresentation Representation { get; set; } = NodeRepresentation.Linear;
        public NodeDisplayNotation DisplayNotation { get; set; } = NodeDisplayNotation.Automatic;
        public int? DisplayPrecision { get; set; }
    }

    internal class StringNode : NodeBase
    {
        public string? PValue { get; set; }
    }

    internal class BooleanNode : NodeBase
    {
        public string? PValue { get; set; }
    }

    internal class CommandNode : NodeBase
    {
        public string? PValue { get; set; }
        public long CommandValue { get; set; }
    }

    internal class EnumerationNode : NodeBase
    {
        public string? PValue { get; set; }
        public long? ConstantValue { get; set; }  // non-null when XML uses node-level <Value> instead of <pValue>
        public Dictionary<string, long> Entries { get; set; } = new Dictionary<string, long>();
        public Dictionary<long, string> SymbolicByValue { get; set; } = new Dictionary<long, string>();
        // Per-entry pIsImplemented / pIsAvailable guards; only entries with guards are stored.
        public Dictionary<string, (string? PIsImplemented, string? PIsAvailable)> EntryGuards { get; set; }
            = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
    }

    // Formula-based computed node — evaluates an expression over named pVariable references.
    internal class IntSwissKnifeNode : NodeBase
    {
        public Dictionary<string, string> Variables { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public string? Formula { get; set; }
    }

    internal class SwissKnifeNode : NodeBase
    {
        public Dictionary<string, string> Variables { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public string? Formula { get; set; }
    }

    // Linear-conversion nodes: output = FormulaTo(pValue) (read) or FormulaFrom (write).
    // IntConverterNode is the integer-valued variant — its result is truncated to long.
    internal abstract class ConverterNodeBase : NodeBase
    {
        public string? PValue { get; set; }
        public string? FormulaTo { get; set; }    // formula applied when reading (input → output)
        public string? FormulaFrom { get; set; }  // formula applied when writing (output → input)
        public Dictionary<string, string> Variables { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal class ConverterNode : ConverterNodeBase { }

    internal class IntConverterNode : ConverterNodeBase { }
}
