namespace garge_api.Dtos.Admin
{
    public class AdminStatsDto
    {
        public int TotalUsers { get; set; }
        public int TotalSensors { get; set; }
        public int TotalSwitches { get; set; }
        public int ActiveAutomations { get; set; }

        public AdminOrderStatsDto Orders { get; set; } = new();
        public AdminSubscriptionStatsDto Subscriptions { get; set; } = new();
    }

    public class AdminOrderStatsDto
    {
        public int Today { get; set; }
        public int ThisWeek { get; set; }
        public int ThisMonth { get; set; }
        public int PendingCapture { get; set; }
        public int FailedOrCancelled { get; set; }
        public long TotalRevenueInOre { get; set; }
        public long MonthRevenueInOre { get; set; }
    }

    public class AdminSubscriptionStatsDto
    {
        public int Active { get; set; }
        public int PendingConfirm { get; set; }
        public int StoppedThisMonth { get; set; }
        public long MonthlyRecurringInOre { get; set; }
    }
}
