using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
    public class UpdateSensorActivityDto
    {
        [Required]
        [MaxLength(100)]
        public required string Title { get; set; }

        public string? Notes { get; set; }

        public DateTime? ActivityDate { get; set; }
    }
}
