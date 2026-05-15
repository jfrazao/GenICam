namespace Bonsai.GenICam
{
    /// <summary>
    /// Describes a GenICam camera device discovered during enumeration.
    /// </summary>
    public class DeviceInfo
    {
        /// <summary>Gets the zero-based global index of this device across all producers.</summary>
        public int GlobalIndex { get; internal set; }

        /// <summary>Gets the GenTL device identifier string.</summary>
        public string ID { get; internal set; } = string.Empty;

        /// <summary>Gets the GenTL interface identifier that hosts this device.</summary>
        public string InterfaceID { get; internal set; } = string.Empty;

        /// <summary>Gets the path to the GenTL producer (.cti file) that reported this device.</summary>
        public string ProducerPath { get; internal set; } = string.Empty;

        /// <summary>Gets the camera vendor name (e.g. <c>Basler</c>).</summary>
        public string Vendor { get; internal set; } = string.Empty;

        /// <summary>Gets the camera model name (e.g. <c>Blackfly S BFS-U3-16S2M</c>).</summary>
        public string Model { get; internal set; } = string.Empty;

        /// <summary>Gets the camera serial number.</summary>
        public string SerialNumber { get; internal set; } = string.Empty;

        /// <summary>Gets the transport layer type (e.g. <c>USB3Vision</c>, <c>GigEVision</c>).</summary>
        public string TLType { get; internal set; } = string.Empty;

        /// <summary>Gets the human-readable display name reported by the producer.</summary>
        public string DisplayName { get; internal set; } = string.Empty;

        /// <summary>Returns a short string identifying the device by index, vendor, model, serial, and transport type.</summary>
        public override string ToString() =>
            $"[{GlobalIndex}] {Vendor} {Model} (S/N: {SerialNumber}, {TLType})";
    }
}
