namespace Bonsai.GenICam
{
    public class DeviceInfo
    {
        public int GlobalIndex { get; internal set; }
        public string ID { get; internal set; }
        public string InterfaceID { get; internal set; }
        public string ProducerPath { get; internal set; }
        public string Vendor { get; internal set; }
        public string Model { get; internal set; }
        public string SerialNumber { get; internal set; }
        public string TLType { get; internal set; }
        public string DisplayName { get; internal set; }

        public override string ToString() =>
            $"[{GlobalIndex}] {Vendor} {Model} (S/N: {SerialNumber}, {TLType})";
    }
}
