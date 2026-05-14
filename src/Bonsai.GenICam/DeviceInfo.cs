namespace Bonsai.GenICam
{
    public class DeviceInfo
    {
        public int GlobalIndex { get; internal set; }
        public string ID { get; internal set; } = string.Empty;
        public string InterfaceID { get; internal set; } = string.Empty;
        public string ProducerPath { get; internal set; } = string.Empty;
        public string Vendor { get; internal set; } = string.Empty;
        public string Model { get; internal set; } = string.Empty;
        public string SerialNumber { get; internal set; } = string.Empty;
        public string TLType { get; internal set; } = string.Empty;
        public string DisplayName { get; internal set; } = string.Empty;

        public override string ToString() =>
            $"[{GlobalIndex}] {Vendor} {Model} (S/N: {SerialNumber}, {TLType})";
    }
}
