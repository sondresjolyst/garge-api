using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
    public class ReportedSettingsDto
    {
        // Matches what the firmware will accept: it refuses anything outside this
        // range, so a report outside it cannot have come from a device running it.
        [Range(60, 86400)]
        public required int SleepSeconds { get; set; }

        public required bool SecurityEnabled { get; set; }

        /// <summary>
        /// The battery floor the device says is in effect, in millivolts. Null means it
        /// has no floor. Absent means firmware that does not report one yet, and the
        /// floor is then not checked.
        /// </summary>
        public int? FloorMillivolts { get; set; }

        [MaxLength(32)]
        public string? Version { get; set; }
    }
}
