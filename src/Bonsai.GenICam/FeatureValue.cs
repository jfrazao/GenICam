namespace Bonsai.GenICam
{
    /// <summary>
    /// Represents a named GenICam feature and its current value.
    /// </summary>
    public class FeatureValue
    {
        /// <summary>Gets the GenICam feature name (e.g. <c>ExposureTime</c>).</summary>
        public string Name { get; }

        /// <summary>Gets the feature value; actual runtime type depends on the node kind (long, double, string, bool, or enum string).</summary>
        public object Value { get; }

        /// <summary>Initializes a new <see cref="FeatureValue"/> with the given name and value.</summary>
        public FeatureValue(string name, object value)
        {
            Name = name;
            Value = value;
        }

        /// <summary>Returns a <c>"Name = Value"</c> representation of this feature.</summary>
        public override string ToString() => $"{Name} = {Value}";
    }
}
