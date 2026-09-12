namespace garge_api.Dtos.Sensor
{
    public class EnforcingRuleDto
    {
        public int Id { get; set; }
        public int TargetId { get; set; }
        public required string TargetName { get; set; }
        public required string Condition { get; set; }
        public double Threshold { get; set; }
    }
}
