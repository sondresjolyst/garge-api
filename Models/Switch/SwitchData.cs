using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Switch
{
    public class SwitchData
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int SwitchId { get; set; }

        [ForeignKey("SwitchId")]
        public Switch? Switch { get; set; }

        [Required]
        public required string Value { get; set; }

        [Required]
        public DateTime Timestamp { get; set; }
    }
}
