namespace garge_api.Dtos.Sensor
{
    public class BatteryHealthDto
    {
        public int Id { get; set; }
        public int SensorId { get; set; }
        public required string Status { get; set; }
        public float Baseline { get; set; }
        public float LastCharge { get; set; }
        public float DropPct { get; set; }
        public int ChargesRecorded { get; set; }
        public DateTime Timestamp { get; set; }
        public DateTime? LastChargedAt { get; set; }
    }
}
