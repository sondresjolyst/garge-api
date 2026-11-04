using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Sensor
{
    /// <summary>
    /// Reading facts for a battery-voltage sensor at one point in time. One row
    /// inserted by <c>BatteryHealthAnalyzerService</c> when an online sensor has
    /// a reading newer than its latest row. Nothing here depends on who owns the sensor. Status and
    /// the values next to it are worked out for each viewer at read time.
    /// </summary>
    public class BatteryHealth
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int SensorId { get; set; }

        [ForeignKey("SensorId")]
        public Sensor? Sensor { get; set; }

        public float CurrentVoltage { get; set; }
        // Null when the last 3 days hold no rest readings.
        public float? RestingMedian { get; set; }
        public bool OnChargerNow { get; set; }
        public float? VoltageMin24h { get; set; }

        [Required]
        public DateTime Timestamp { get; set; }
    }
}
