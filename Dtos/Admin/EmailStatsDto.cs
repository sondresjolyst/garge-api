namespace garge_api.Dtos.Admin
{
    public class EmailStatsDto
    {
        public long Requests { get; set; }
        public long Delivered { get; set; }
        public long HardBounces { get; set; }
        public long SoftBounces { get; set; }
        public long SpamReports { get; set; }
        public long Blocked { get; set; }
        public long Invalid { get; set; }
        public int Days { get; set; }
    }
}
