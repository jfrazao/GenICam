namespace Bonsai.GenICam
{
    public class FeatureValue
    {
        public string Name { get; }
        public object Value { get; }

        public FeatureValue(string name, object value)
        {
            Name = name;
            Value = value;
        }

        public override string ToString() => $"{Name} = {Value}";
    }
}
