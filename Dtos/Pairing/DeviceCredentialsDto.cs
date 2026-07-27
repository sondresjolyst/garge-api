namespace garge_api.Dtos.Pairing
{
    /// <summary>MQTT broker credentials handed to a device during provisioning. The plaintext
    /// password is returned exactly once; only its PBKDF2 hash is stored.</summary>
    public class DeviceCredentialsDto
    {
        public required string Username { get; set; }
        public required string Password { get; set; }
    }
}
