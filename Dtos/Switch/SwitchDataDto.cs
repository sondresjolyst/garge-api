namespace garge_api.Dtos.Switch
{
    public class SwitchDataDto
    {
        public int Id { get; set; }
        public int SwitchId { get; set; }
        public string Value { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }
}