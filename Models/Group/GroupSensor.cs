namespace garge_api.Models.Group
{
    public class GroupSensor
    {
        public int GroupId { get; set; }
        public Group Group { get; set; } = null!;

        public int SensorId { get; set; }
        public Sensor.Sensor Sensor { get; set; } = null!;
    }
}
