using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Group
{
    public class Group
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        public required string Name { get; set; }

        /// <summary>
        /// Icon key: motorcycle | car | boat | house | building | other
        /// </summary>
        [MaxLength(50)]
        public string? Icon { get; set; }

        [Required]
        public required string UserId { get; set; }

        public ICollection<GroupSensor> GroupSensors { get; set; } = new List<GroupSensor>();
    }
}
