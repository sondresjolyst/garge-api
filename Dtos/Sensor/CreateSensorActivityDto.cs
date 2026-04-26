using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
    public class CreateSensorActivityDto
    {
        [Required]
        [MaxLength(100)]
        public required string Title { get; set; }

        public string? Notes { get; set; }

        /// <summary>
        /// When the activity occurred. Defaults to the current UTC time if not supplied.
        /// </summary>
        public DateTime? ActivityDate { get; set; }
    }
}
