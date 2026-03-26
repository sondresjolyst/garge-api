using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Sensor
{
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

        public float Baseline { get; set; }
        public float LastCharge { get; set; }
        public float DropPct { get; set; }
        public int ChargesRecorded { get; set; }

        [Required]
        public DateTime Timestamp { get; set; }

        public DateTime? LastChargedAt { get; set; }
    }
}
