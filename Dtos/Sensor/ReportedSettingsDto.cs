using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
    public class ReportedSettingsDto
    {
        [Range(1, 86400)]
        public required int SleepSeconds { get; set; }

        public required bool SecurityEnabled { get; set; }

        [MaxLength(32)]
        public string? Version { get; set; }
    }
}
