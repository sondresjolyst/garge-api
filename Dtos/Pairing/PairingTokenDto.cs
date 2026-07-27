namespace garge_api.Dtos.Pairing
{
    public class PairingTokenDto
    {
        public required string Token { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
