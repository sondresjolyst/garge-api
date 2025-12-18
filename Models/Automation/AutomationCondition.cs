using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Automation
{
    public class AutomationCondition
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int AutomationRuleId { get; set; }

        [ForeignKey("AutomationRuleId")]
        public AutomationRule AutomationRule { get; set; } = null!;

        [Required]
        public required string SensorType { get; set; }

        [Required]
        public int SensorId { get; set; }

        [Required]
        public required string Condition { get; set; }

        [Required]
        public double Threshold { get; set; }
    }
}
