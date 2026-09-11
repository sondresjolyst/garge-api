namespace garge_api.Dtos.Sensor
{
    public class DeviceSettingsDto
    {
        public int SensorId { get; set; }
        public required string DeviceName { get; set; }
        public int SleepSeconds { get; set; }
        public bool SecurityEnabled { get; set; }
        public int? FloorMillivolts { get; set; }
        public long Version { get; set; }
    }
}
