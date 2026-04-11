namespace garge_api.Models.Group
{
    public class GroupSwitch
    {
        public int GroupId { get; set; }
        public Group Group { get; set; } = null!;

        public int SwitchId { get; set; }
        public Switch.Switch Switch { get; set; } = null!;
    }
}
