using garge_api.Constants;
using SjolystInnovation.AppKit.Email;

namespace garge_api.Services
{
    /// <summary>The Brevo email stats for the admin page, served by <c>MapAppKitEmailStats</c>.</summary>
    public static class EmailStatsEndpoint
    {
        /// <summary>The route garge-app calls.</summary>
        public const string Path = "/api/admin/email-stats";

        /// <summary>Only admins may read the stats.</summary>
        public static void Configure(AppKitEmailStatsEndpointOptions options) => options.Policy = RoleNames.Admin;
    }
}
