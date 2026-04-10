namespace garge_api.Dtos.Group
{
    public class GroupDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Icon { get; set; }
        public List<int> SensorIds { get; set; } = new();
    }
}
