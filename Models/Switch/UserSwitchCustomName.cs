namespace garge_api.Models.Switch
{
    public class UserSwitchCustomName
    {
        public string UserId { get; set; } = default!;
        public int SwitchId { get; set; }
        public string CustomName { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
        public User User { get; set; } = default!;
        public Switch Switch { get; set; } = default!;
    }
}
