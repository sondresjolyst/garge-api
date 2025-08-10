namespace garge_api.Dtos.Mqtt
{
    public class CreateDiscoveredDeviceDto
    {
        public required string DiscoveredBy { get; set; }
        public required string Target { get; set; }
        public required string Type { get; set; }
        public required DateTime Timestamp { get; set; }
    }

}
