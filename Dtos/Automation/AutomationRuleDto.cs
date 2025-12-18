namespace garge_api.Dtos.Automation
{
    public class AutomationRuleDto
    {
        public int Id { get; set; }
        public required string TargetType { get; set; }
        public int TargetId { get; set; }
        
        // Multiple conditions
        public required List<AutomationConditionDto> Conditions { get; set; }
        public string? LogicalOperator { get; set; } // "AND" or "OR"
        
        public required string Action { get; set; }
    }
}
