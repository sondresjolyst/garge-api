namespace garge_api.Dtos.Mqtt
{
    public class CreateEMQXMqttUserDto
    {
        public bool? IsSuperuser { get; set; }
        public required string Username { get; set; }
        public required string Password { get; set; }
    }
}
