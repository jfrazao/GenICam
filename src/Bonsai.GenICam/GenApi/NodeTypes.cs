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
    }

    // Direct register nodes (hold the actual address + length)
    internal class IntRegNode : NodeBase
    {
        public ulong Address { get; set; }        // direct <Address> value (0 if absent)
        public string[]? PAddresses { get; set; }  // zero or more <pAddress> refs, all summed into Address
        public int Length { get; set; }            // bytes
        public bool Unsigned { get; set; } = true;
        public bool LittleEndian { get; set; } = true;
    }

    internal class FloatRegNode : NodeBase
    {
        public ulong Address { get; set; }
        public string[]? PAddresses { get; set; }
        public int Length { get; set; }            // 4 = float, 8 = double
        public bool LittleEndian { get; set; } = true;
    }

    internal class StringRegNode : NodeBase
    {
        public ulong Address { get; set; }
        public string[]? PAddresses { get; set; }
        public int Length { get; set; }
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
        public string? PMin { get; set; }
        public string? PMax { get; set; }
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
        public Dictionary<string, long> Entries { get; set; } = new Dictionary<string, long>();
        public Dictionary<long, string> SymbolicByValue { get; set; } = new Dictionary<long, string>();
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

    // Linear-conversion nodes: output = (pValue - Offset) / Gain  (read) or inverse (write).
    internal class ConverterNode : NodeBase
    {
        public string? PValue { get; set; }
        public string? FormulaTo { get; set; }    // formula applied when reading (input → output)
        public string? FormulaFrom { get; set; }  // formula applied when writing (output → input)
        public Dictionary<string, string> Variables { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal class IntConverterNode : NodeBase
    {
        public string? PValue { get; set; }
        public string? FormulaTo { get; set; }
        public string? FormulaFrom { get; set; }
        public Dictionary<string, string> Variables { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
