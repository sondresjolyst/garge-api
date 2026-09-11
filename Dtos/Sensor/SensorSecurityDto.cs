namespace garge_api.Dtos.Sensor
{
    public class SensorSecurityDto
    {
        public int SensorId { get; set; }
        public bool Enabled { get; set; }
        public int ThresholdMinutes { get; set; }
        public int RequestedSleepSeconds { get; set; }
        public int? AppliedSleepSeconds { get; set; }
        public DateTime? ArmedAt { get; set; }
        public DateTime? LastReportedAt { get; set; }

        /// <summary><c>off</c>, <c>pending</c>, <c>armed</c>, <c>paused_low_battery</c> or <c>offline</c>.</summary>
        public required string State { get; set; }

        /// <summary><c>firmware_too_old</c>, <c>awaiting_wake</c>, <c>low_battery</c>, or null.</summary>
        public string? Reason { get; set; }

        public EnforcingRuleDto? EnforcingRule { get; set; }
        public bool IsOwner { get; set; }
    }
}
