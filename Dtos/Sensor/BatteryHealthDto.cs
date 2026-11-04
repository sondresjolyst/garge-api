namespace garge_api.Dtos.Sensor
{
    public class BatteryHealthDto
    {
        public int Id { get; set; }
        public int SensorId { get; set; }
        public required string Status { get; set; }

        public float CurrentVoltage { get; set; }
        public float? RestingMedian { get; set; }
        public bool OnChargerNow { get; set; }
        public DateTime? LastFullChargeAt { get; set; }
        public float? LastFullChargePeak { get; set; }
        public float? VoltageMin24h { get; set; }
        public int FullChargesLast30d { get; set; }
        public float? DailyDropPctPerWeek { get; set; }
        public float? SelfDischargePctPerWeek { get; set; }

        public DateTime Timestamp { get; set; }
    }

    public class BatteryChargeEventDto
    {
        public int Id { get; set; }
        public int SensorId { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime EndedAt { get; set; }
        public float PeakVoltage { get; set; }
        // Null when the 7 days before the start are not inside one of the viewer's periods.
        public float? RestingAtTime { get; set; }
        public int DurationMinutes { get; set; }
        public bool IsFull { get; set; }
        public float? SettledVoltage { get; set; }
        public float? SelfDischargePctPerWeek { get; set; }
    }
}
