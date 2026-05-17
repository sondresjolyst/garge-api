using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Sensor
{
    /// <summary>
    /// Snapshot of a battery-voltage sensor's analyzer-computed health.
    /// One row inserted by <c>BatteryHealthAnalyzerService</c> per analyzer
    /// run (event-driven on new SensorData). The latest row is what the
    /// UI reads.
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
        public float RestingMedian { get; set; }
        public float PeakResting { get; set; }
        public bool OnChargerNow { get; set; }
        public DateTime? LastFullChargeAt { get; set; }
        public float? LastFullChargePeak { get; set; }
        public float? VoltageMin24h { get; set; }
        public int FullChargesLast30d { get; set; }
        public float DailyDropPctPerWeek { get; set; }
        public float? ChargeAcceptanceRatio { get; set; }

        [Required]
        public DateTime Timestamp { get; set; }
    }
}
