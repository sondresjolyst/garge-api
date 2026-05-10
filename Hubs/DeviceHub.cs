using System.Security.Claims;
using garge_api.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace garge_api.Hubs
{
    [Authorize]
    public class DeviceHub : Hub
    {
        public const string BridgeGroup = "device-bridges";
        // Service-only role for processes that bridge hub events to an external transport
        // (e.g., garge-operator → MQTT). Should not be granted to humans.
        public const string BridgeRole = "DeviceBridge";

        public static string UserGroup(string userId) => $"user-{userId}";

        private readonly ILogger<DeviceHub> _logger;
        private readonly IHubConnectionTracker _tracker;

        public DeviceHub(ILogger<DeviceHub> logger, IHubConnectionTracker tracker)
        {
            _logger = logger;
            _tracker = tracker;
        }

        public override async Task OnConnectedAsync()
        {
            var isBridge = Context.User?.IsInRole(BridgeRole) ?? false;
            if (isBridge)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, BridgeGroup);
                _logger.LogInformation("DeviceHub: connection {Conn} joined {Group}", Context.ConnectionId, BridgeGroup);
            }
            else
            {
                var userId = Context.User?.UserId();
                if (string.IsNullOrEmpty(userId))
                {
                    Context.Abort();
                    return;
                }
                await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
                _tracker.Add(userId, Context.ConnectionId);
                _logger.LogInformation("DeviceHub: connection {Conn} joined {Group}", Context.ConnectionId, UserGroup(userId));
            }
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var userId = Context.User?.UserId();
            if (!string.IsNullOrEmpty(userId))
            {
                _tracker.Remove(userId, Context.ConnectionId);
            }
            await base.OnDisconnectedAsync(exception);
        }
    }
}
