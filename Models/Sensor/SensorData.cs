using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Sensor
{
    public class SensorData
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int SensorId { get; set; }

        [ForeignKey("SensorId")]
        public Sensor? Sensor { get; set; }

        [Required]
        public required string Value { get; set; }

        [Required]
        public DateTime Timestamp { get; set; }
    }
}
