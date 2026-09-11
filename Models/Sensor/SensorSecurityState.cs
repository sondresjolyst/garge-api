using System.ComponentModel.DataAnnotations;

namespace garge_api.Models.Sensor
{
    public class SensorSecurityState
    {
        public int SensorId { get; set; }
        public int RequestedSleepSeconds { get; set; } = Constants.SecurityMode.LongSleepSeconds;
        public DateTime RequestedAt { get; set; }
        public int? FloorMillivolts { get; set; }
        public int? AppliedSleepSeconds { get; set; }
        public DateTime? AppliedAt { get; set; }
        public bool SecurityModeReported { get; set; }

        [MaxLength(32)]
        public string? ReportedFirmwareVersion { get; set; }

        public DateTime? ArmedAt { get; set; }
        public DateTime? OfflineDisarmedAt { get; set; }
        public DateTime? LastPublishedAt { get; set; }
        public Sensor Sensor { get; set; } = default!;
    }
}
