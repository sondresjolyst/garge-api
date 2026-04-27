using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Swashbuckle.AspNetCore.Annotations;

namespace garge_api.Models.Sensor
{
    public class SensorActivity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [SwaggerSchema(ReadOnly = true)]
        public int Id { get; set; }

        [Required]
        public int SensorId { get; set; }

        [ForeignKey("SensorId")]
        public Sensor? Sensor { get; set; }

        [Required]
        public string UserId { get; set; } = default!;

        [Required]
        [MaxLength(100)]
        public required string Title { get; set; }

        public string? Notes { get; set; }

        /// <summary>
        /// Optional odometer reading (km) at the time of the activity.
        /// </summary>
        public int? OdometerKm { get; set; }

        [Required]
        public DateTime ActivityDate { get; set; }

        [Required]
        public DateTime CreatedAt { get; set; }

        public DateTime? UpdatedAt { get; set; }
    }
}
