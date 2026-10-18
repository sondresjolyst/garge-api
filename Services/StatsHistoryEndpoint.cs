using garge_api.Constants;
using SjolystInnovation.AppKit.AdminStats;

namespace garge_api.Services
{
    /// <summary>The daily stats history for the admin chart, served by <c>MapAppKitDailyStats</c> at <c>{Prefix}/history</c>.</summary>
    public static class StatsHistoryEndpoint
    {
        /// <summary>The route prefix garge-app calls.</summary>
        public const string Prefix = "/api/admin/stats";

        /// <summary>Only admins may read the history.</summary>
        public static void Configure(AppKitDailyStatsEndpointOptions options) => options.Policy = RoleNames.Admin;
    }
}
