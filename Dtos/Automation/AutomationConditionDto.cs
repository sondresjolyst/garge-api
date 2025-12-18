namespace garge_api.Dtos.Automation
{
    public class AutomationConditionDto
    {
        public int? Id { get; set; }
        public required string SensorType { get; set; }
        public int SensorId { get; set; }
        public required string Condition { get; set; }
        public double Threshold { get; set; }
    }
}
