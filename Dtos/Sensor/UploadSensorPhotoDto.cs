using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
    public class UploadSensorPhotoDto
    {
        [Required]
        public required string Data { get; set; }

        [Required]
        [MaxLength(50)]
        [RegularExpression(@"^image/(jpeg|png|webp|gif)$",
            ErrorMessage = "ContentType must be image/jpeg, image/png, image/webp, or image/gif.")]
        public required string ContentType { get; set; }
    }
}
