namespace garge_api.Dtos.Admin
{
    public class AdminStatsDto
    {
        public int TotalUsers { get; set; }
        public int TotalSensors { get; set; }
        public int TotalSwitches { get; set; }
        public int ActiveAutomations { get; set; }
    }
}
