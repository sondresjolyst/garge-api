using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Automation
{
    public class CreateAutomationRuleDto
    {
        [Required]
        public required string TargetType { get; set; }

        [Required]
        public int TargetId { get; set; }

        [Required]
        public required string SensorType { get; set; }

        [Required]
        public int SensorId { get; set; }

        [Required]
        public required string Condition { get; set; }

        [Required]
        public double Threshold { get; set; }

        [Required]
        public required string Action { get; set; }

        public bool IsEnabled { get; set; } = true;

        public string? ElectricityPriceCondition { get; set; }
        public double? ElectricityPriceThreshold { get; set; }
        public string? ElectricityPriceArea { get; set; }
        public string? ElectricityPriceOperator { get; set; }
    }
}
