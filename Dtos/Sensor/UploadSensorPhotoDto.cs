using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
    public class UploadSensorPhotoDto
    {
        [Required]
        public required string Data { get; set; }

        [Required]
        [MaxLength(50)]
        public required string ContentType { get; set; }
    }
}
