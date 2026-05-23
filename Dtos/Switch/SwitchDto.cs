using garge_api.Models;

namespace garge_api.Dtos.Switch
{
    public class SwitchDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string? CustomName { get; set; }
        public string? RegistrationCode { get; set; }

        /// <summary>The caller's relationship to this switch: owner (or admin), edit, or read.</summary>
        public string Access { get; set; } = DeviceAccess.Owner;
    }
}