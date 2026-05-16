using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Swashbuckle.AspNetCore.Annotations;

namespace garge_api.Models.Sensor
{
    public class Sensor
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [SwaggerSchema(ReadOnly = true)]
        public int Id { get; set; }

        [Required]
        [MaxLength(50)]
        public required string Name { get; set; }

        [Required]
        [MaxLength(50)]
        public required string Type { get; set; }

        [Required]
        [MaxLength(50)]
        public required string Role { get; set; }

        [Required]
        public required string RegistrationCode { get; set; }
        
        [Required]
        [MaxLength(50)]
        public required string DefaultName { get; set; }

        [Required]
        public required string ParentName { get; set; }

        // User-supplied additive offset (volts) for voltage sensors with
        // calibration drift. Frontend displays `value + offset` as the
        // "actual" voltage; health logic itself is ratio-based so doesn't
        // use this. Null means uncalibrated.
        public float? CalibrationOffsetV { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
