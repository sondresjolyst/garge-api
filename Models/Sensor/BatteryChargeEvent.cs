using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Sensor
{
    /// <summary>
    /// One detected charge event for a voltage sensor: a contiguous window
    /// where voltage stayed elevated above the sensor's own resting median
    /// long enough and high enough to count as a real charge cycle.
    ///
    /// Detected by <c>BatteryHealthAnalyzerService</c> from <c>SensorData</c>;
    /// de-duplicated by <c>(SensorId, StartedAt)</c>.
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

        [Required]
        public float RestingAtTime { get; set; }

        // peak / restingAtTime — health uses this for "charge acceptance" signal.
        [Required]
        public float PeakRatio { get; set; }

        [Required]
        public int DurationMinutes { get; set; }
    }
}
