using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
    public class CreateSensorDto
    {
        [Required]
        [MaxLength(50)]
        public required string Name { get; set; }

        [Required]
        [MaxLength(50)]
        public required string Type { get; set; }

        [MaxLength(50)]
        public string? CustomName { get; set; }
    }
}
