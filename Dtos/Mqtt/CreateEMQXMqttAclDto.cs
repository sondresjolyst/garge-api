namespace garge_api.Dtos.Mqtt
{
    public class CreateEMQXMqttAclDto
    {
        public required string Username { get; set; }
        public required string Permission { get; set; }
        public required string Action { get; set; }
        public required string Topic { get; set; }
        public short? Qos { get; set; }
        public short? Retain { get; set; }
    }
}
