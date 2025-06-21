using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
    public class UpdateSensorDto
    {
        [Required]
        [MaxLength(50)]
        public required string Name { get; set; }

        [Required]
        [MaxLength(50)]
        public required string Type { get; set; }

        [Required]
        [MaxLength(50)]
        public required string Role { get; set; }
    }
}
