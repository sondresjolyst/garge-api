using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Automation
{
    public class UpdateAutomationRuleDto
    {
        [Required]
        public required string TargetType { get; set; }
        
        [Required]
        public int TargetId { get; set; }
        
        // Multiple conditions (required)
        [Required]
        public required List<AutomationConditionDto> Conditions { get; set; }
        
        public string? LogicalOperator { get; set; } // "AND" or "OR", defaults to "AND"
        
        [Required]
        public required string Action { get; set; }
    }
}
