using Bonsai.GenICam.GenApi;

namespace Bonsai.GenICam
{
    /// <summary>
    /// Camera-selection surface shared by operators that open a device (<see cref="GenICamDevice"/>,
    /// <see cref="ListFeatureValues"/>). Used by the camera/serial dropdown editors and the feature
    /// editor to enumerate and connect to the configured device, and to reuse a live node map when the
    /// workflow is running.
    /// </summary>
    internal interface IGenICamSource
    {
        string? ProducerPath { get; }
        int DeviceIndex { get; }
        string? CameraModel { get; }
        string? SerialNumber { get; }
        NodeMap? LiveNodeMap { get; }
    }
}
