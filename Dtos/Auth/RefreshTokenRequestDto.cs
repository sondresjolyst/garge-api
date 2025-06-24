namespace garge_api.Dtos.Auth
{
    public class RefreshTokenRequestDto
    {
        public required string Token { get; set; }
        public required string RefreshToken { get; set; }
    }
}
