using garge_api.Models;

namespace garge_api.Dtos.Common
{
    /// <summary>Request to share a device (sensor or switch) with another Garge user by their account email.</summary>
    public class ShareRequestDto
    {
        public required string Email { get; set; }

        /// <summary>Read (view only) or Edit (also control/automate).</summary>
        public SharePermission Permission { get; set; } = SharePermission.Read;
    }
}
