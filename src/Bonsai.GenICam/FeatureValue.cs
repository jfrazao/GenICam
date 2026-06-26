using System.Globalization;

namespace Bonsai.GenICam
{
    /// <summary>Discriminates the value type carried by a <see cref="FeatureValue"/>.</summary>
    public enum FeatureValueType
    {
        /// <summary>64-bit IEEE 754 float (maps to <see cref="double"/>).</summary>
        Float,
        /// <summary>64-bit signed integer (maps to <see cref="long"/>).</summary>
        Integer,
        /// <summary>Boolean (maps to <see cref="bool"/>).</summary>
        Boolean,
        /// <summary>Raw string value.</summary>
        String,
        /// <summary>Enumeration entry — value is the entry name string.</summary>
        Enumeration
    }

    /// <summary>
    /// Represents a named GenICam feature and its current value.
    /// </summary>
    public class FeatureValue
    {
        /// <summary>Gets the GenICam feature name (e.g. <c>ExposureTime</c>).</summary>
        public string Name { get; }

        /// <summary>Gets the declared type of this feature value.</summary>
        public FeatureValueType Type { get; }

        /// <summary>Gets the feature value; actual runtime type depends on <see cref="Type"/>.</summary>
        public object Value { get; }

        /// <summary>
        /// Initializes a <see cref="FeatureValue"/> and infers <see cref="Type"/> from the runtime
        /// type of <paramref name="value"/>. Used by <c>ListFeatureValues</c> and <c>NodeMap</c>.
        /// </summary>
        public FeatureValue(string name, object value)
        {
            Name = name;
            Value = value;
            Type = value switch
            {
                double _ => FeatureValueType.Float,
                long   _ => FeatureValueType.Integer,
                bool   _ => FeatureValueType.Boolean,
                _        => FeatureValueType.String
            };
        }

        /// <summary>
        /// Initializes a <see cref="FeatureValue"/> with an explicit <see cref="FeatureValueType"/>.
        /// </summary>
        public FeatureValue(string name, FeatureValueType type, object value)
        {
            Name = name;
            Type = type;
            Value = value;
        }

        /// <summary>Returns the value as <see cref="double"/>. Throws if <see cref="Type"/> is not <see cref="FeatureValueType.Float"/>.</summary>
        public double AsFloat() => (double)Value;

        /// <summary>Returns the value as <see cref="long"/>. Throws if <see cref="Type"/> is not <see cref="FeatureValueType.Integer"/>.</summary>
        public long AsInt() => (long)Value;

        /// <summary>Returns the value as <see cref="bool"/>. Throws if <see cref="Type"/> is not <see cref="FeatureValueType.Boolean"/>.</summary>
        public bool AsBool() => (bool)Value;

        /// <summary>Returns the value as <see cref="string"/>. Works for <see cref="FeatureValueType.String"/> and <see cref="FeatureValueType.Enumeration"/>.</summary>
        public string AsString() => (string)Value;

        /// <summary>
        /// Formats <see cref="Value"/> as the invariant-culture string carried on the GenICam message
        /// bus (write payloads and read responses): floats/integers in invariant culture, booleans as
        /// <c>"True"</c>/<c>"False"</c>, everything else via <see cref="object.ToString"/>.
        /// </summary>
        internal string ToPayloadString() => Value switch
        {
            double d => d.ToString(CultureInfo.InvariantCulture),
            long   l => l.ToString(CultureInfo.InvariantCulture),
            bool   b => b ? "True" : "False",
            _        => Value?.ToString() ?? string.Empty
        };

        /// <summary>Returns a <c>"Name = Value"</c> representation of this feature.</summary>
        public override string ToString() => $"{Name} = {Value}";
    }
}
