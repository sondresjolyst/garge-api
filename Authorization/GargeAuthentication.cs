using garge_api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace garge_api.Authorization
{
    /// <summary>
    /// The bearer scheme garge-api uses. Requests carry the session token in the Authorization header.
    /// The device hub also takes a ticket from <see cref="IHubTicketService"/>, which browsers send in the
    /// query string because a WebSocket cannot carry a header.
    /// </summary>
    public static class GargeAuthentication
    {
        public const string DeviceHubPath = "/hubs/devices";

        public static AuthenticationBuilder AddGargeJwtBearer(this IServiceCollection services)
        {
            var builder = services.AddAppKitJwtBearer<User>(u => !u.IsDeleted);

            services.TryAddSingleton(TimeProvider.System);
            services.AddMemoryCache();
            services.AddSingleton<IHubTicketService, HubTicketService>();

            // PostConfigure runs after AppKit's Configure, so it wraps the events AppKit set.
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var readToken = options.Events.OnMessageReceived;
                options.Events.OnMessageReceived = context =>
                {
                    if (!context.Request.Path.StartsWithSegments(DeviceHubPath) || ReadTicket(context.Request) is not { } ticket)
                        return readToken(context);

                    var tickets = context.HttpContext.RequestServices.GetRequiredService<IHubTicketService>();
                    var principal = tickets.Redeem(ticket, context.Request.Query["id"].ToString());
                    if (principal is null)
                    {
                        context.Fail("The hub ticket is unknown, expired, or in use by another connection.");
                        return Task.CompletedTask;
                    }
                    context.Principal = principal;
                    context.Success();
                    return Task.CompletedTask;
                };
            });
            return builder;
        }

        /// <summary>
        /// Reads a hub ticket. A token in the query string is always read as a ticket, so a session token there
        /// is refused. A header token without dots is a ticket too, while a JWT in the header goes to the
        /// normal bearer check.
        /// </summary>
        private static string? ReadTicket(HttpRequest request)
        {
            if (request.Query.TryGetValue("access_token", out var query))
                return query.ToString();

            const string prefix = "Bearer ";
            var header = request.Headers.Authorization.ToString();
            if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;
            var token = header[prefix.Length..].Trim();
            return token.Length > 0 && !token.Contains('.') ? token : null;
        }
    }
}
