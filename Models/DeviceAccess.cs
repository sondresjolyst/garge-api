namespace garge_api.Models
{
    /// <summary>
    /// The caller's relationship to a device, surfaced as <c>SensorDto.Access</c>. Single source for the
    /// labels and the owner/permission → label mapping; the client mirrors these strings.
    /// </summary>
    public static class DeviceAccess
    {
        public const string Owner = "owner";
        public const string Edit = "edit";
        public const string Read = "read";

        /// <summary>Maps an owner flag + share permission to the access label.</summary>
        public static string From(bool isOwner, SharePermission permission) =>
            isOwner ? Owner : permission == SharePermission.Edit ? Edit : Read;
    }
}
