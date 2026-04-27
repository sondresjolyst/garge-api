using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using garge_api.Models.Admin;
using Swashbuckle.AspNetCore.Annotations;

namespace garge_api.Models.Sensor
{
    public class SensorPhoto
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [SwaggerSchema(ReadOnly = true)]
        public int Id { get; set; }

        [Required]
        public int SensorId { get; set; }

        [Required]
        public required string UserId { get; set; }

        [Required]
        public required string Data { get; set; }

        [Required]
        [MaxLength(50)]
        public required string ContentType { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey(nameof(SensorId))]
        public Sensor? Sensor { get; set; }

        [ForeignKey(nameof(UserId))]
        public User? User { get; set; }
    }
}
