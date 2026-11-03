using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Sensor
{
    /// <summary>
    /// Snapshot of a battery-voltage sensor's analyzer-computed health.
    /// One row inserted by <c>BatteryHealthAnalyzerService</c> per analyzer
    /// run (event-driven on new SensorData). The latest row is what the
    /// UI reads. An offline sensor gets no new rows, so its last one stands.
    /// </summary>
    public class BatteryHealth
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int SensorId { get; set; }

        [ForeignKey("SensorId")]
        public Sensor? Sensor { get; set; }

        [Required]
        [MaxLength(20)]
        public required string Status { get; set; }

        public float CurrentVoltage { get; set; }
        // Null when the last 3 days hold no rest readings.
        public float? RestingMedian { get; set; }
        public bool OnChargerNow { get; set; }
        public DateTime? LastFullChargeAt { get; set; }
        public float? LastFullChargePeak { get; set; }
        public float? VoltageMin24h { get; set; }
        public int FullChargesLast30d { get; set; }
        // Cycle-anchored slope (% per week of mean). Null when fewer than the
        // minimum number of settled full charges have been collected.
        public float? DailyDropPctPerWeek { get; set; }
        // Information only, status never uses it. Null until a rest of at least 15 days exists.
        public float? SelfDischargePctPerWeek { get; set; }

        [Required]
        public DateTime Timestamp { get; set; }
    }
}
