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

        /// <summary>
        /// Whether the device behind this sensor runs firmware that takes Garge Security
        /// settings. Null until the bridge has seen one of its config messages.
        /// </summary>
        public bool? SecurityCapable { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
