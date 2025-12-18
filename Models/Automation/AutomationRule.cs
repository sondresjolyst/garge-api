namespace garge_api.Models.Automation
{
    public class AutomationRule
    {
        public int Id { get; set; }
        public required string TargetType { get; set; }
        public int TargetId { get; set; }
        public List<AutomationCondition> Conditions { get; set; } = new();
        public string? LogicalOperator { get; set; }
        public required string Action { get; set; }
    }
}
