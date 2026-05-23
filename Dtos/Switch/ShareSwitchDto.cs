using garge_api.Models;

namespace garge_api.Dtos.Switch
{
    /// <summary>Request to share a switch with another Garge user (by their account email).</summary>
    public class ShareSwitchDto
    {
        public required string Email { get; set; }

        /// <summary>Read (view only) or Edit (also control/automate).</summary>
        public SharePermission Permission { get; set; } = SharePermission.Read;
    }

    /// <summary>A single recipient of a shared switch, returned to the owner.</summary>
    public class SwitchShareDto
    {
        public required string UserId { get; set; }
        public required string Email { get; set; }
        public required string FirstName { get; set; }
        public required string LastName { get; set; }
        public SharePermission Permission { get; set; }
        public DateTime SharedAt { get; set; }
    }
}
