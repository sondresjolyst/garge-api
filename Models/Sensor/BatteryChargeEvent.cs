using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Sensor
{
    /// <summary>
    /// One finished charge for a voltage sensor. Together these rows are the
    /// saved charge history. A charge still open at the latest reading is not stored.
    ///
    /// Detected by <c>BatteryHealthAnalyzerService</c> from <c>SensorData</c>,
    /// unique by <c>(SensorId, StartedAt)</c>. See docs/battery-health.md.
    /// </summary>
    public class BatteryChargeEvent
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int SensorId { get; set; }

        [ForeignKey(nameof(SensorId))]
        public Sensor? Sensor { get; set; }

        [Required]
        public DateTime StartedAt { get; set; }

        [Required]
        public DateTime EndedAt { get; set; }

        [Required]
        public float PeakVoltage { get; set; }

        // The baseline that was frozen when the charge started.
        [Required]
        public float RestingAtTime { get; set; }

        [Required]
        public int DurationMinutes { get; set; }

        // Held near its plateau long enough to count as a full charge.
        [Required]
        public bool IsFull { get; set; }

        // Median 12 to 36 hours after the end. Null with too few readings.
        public float? SettledVoltage { get; set; }

        // For the rest after this charge, once that rest has lasted long enough. Full charges only.
        public float? SelfDischargePctPerWeek { get; set; }
    }
}
