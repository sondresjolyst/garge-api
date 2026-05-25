using garge_api.Models;

namespace garge_api.Dtos.Common
{
    /// <summary>A single recipient of a shared device (sensor or switch), returned to the owner.</summary>
    public class ShareRecipientDto
    {
        public required string UserId { get; set; }
        public required string Email { get; set; }
        public required string FirstName { get; set; }
        public required string LastName { get; set; }
        public SharePermission Permission { get; set; }
        public DateTime SharedAt { get; set; }
    }
}
