using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
    public class ReportedSettingsDto
    {
        // Wide on purpose: a report outside the range the firmware can produce still
        // has to reach ApplyAckAsync, which disarms on it. Rejecting it here would
        // leave the sensor reading armed on settings no device confirmed.
        [Range(1, 86400)]
        public required int SleepSeconds { get; set; }

        public required bool SecurityEnabled { get; set; }

        /// <summary>
        /// True when the device reported a battery floor, false when the bridge has
        /// nothing to report because the firmware does not send one. The floor is
        /// compared only when this is true.
        /// </summary>
        public bool FloorReported { get; set; }

        /// <summary>
        /// The battery floor the device says is in effect, in millivolts, or null when
        /// it has none. Meaningful only when FloorReported is true.
        /// </summary>
        public int? FloorMillivolts { get; set; }

        [MaxLength(32)]
        public string? Version { get; set; }
    }
}
