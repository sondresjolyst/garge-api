using garge_api.Models;
using garge_api.Models.Mqtt;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Tracks what a target device is meant to be doing versus what it was last seen doing. A
    /// command on a device's <c>/set</c> topic is QoS 0 and unretained, so one published while no
    /// gateway holds the lease reaches nobody and is gone. Holding the intent lets the operator
    /// reissue it until the device is observed to agree, and report a failure when it never does.
    /// </summary>
    public interface IDeviceCommandService
    {
        /// <summary>Records the state a target should be in. Resets the retry count.</summary>
        Task<DeviceDesiredState> SetDesiredStateAsync(string target, string desiredState, CancellationToken cancellationToken = default);

        /// <summary>
        /// Records the state a target was observed in, taken from the device's own push rather
        /// than from a gateway's memory, and settles the intent once the two agree.
        /// </summary>
        Task<DeviceDesiredState?> RecordObservedStateAsync(string target, string observedState, CancellationToken cancellationToken = default);

        /// <summary>
        /// Intents still waiting to be honoured and not yet past their attempt limit, oldest
        /// first, for the operator to publish.
        /// </summary>
        Task<IReadOnlyList<DeviceDesiredState>> PendingAsync(CancellationToken cancellationToken = default);

        /// <summary>Counts one publish of an intent, so a command cannot be retried forever.</summary>
        Task<bool> RecordAttemptAsync(string target, CancellationToken cancellationToken = default);
    }

    public class DeviceCommandService : IDeviceCommandService
    {
        /// <summary>
        /// How many publishes an intent gets before it is abandoned. A handover takes at most one
        /// lease duration, so this has to cover a gap of that order at the operator's retry
        /// interval, and still end rather than retry an impossible command forever.
        /// </summary>
        public const int MaxAttempts = 10;

        private readonly ApplicationDbContext _context;
        private readonly TimeProvider _time;
        private readonly ILogger<DeviceCommandService> _logger;

        public DeviceCommandService(ApplicationDbContext context, ILogger<DeviceCommandService> logger, TimeProvider? time = null)
        {
            _context = context;
            _logger = logger;
            _time = time ?? TimeProvider.System;
        }

        public async Task<DeviceDesiredState> SetDesiredStateAsync(string target, string desiredState, CancellationToken cancellationToken = default)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var row = await _context.DeviceDesiredStates.FirstOrDefaultAsync(d => d.Target == target, cancellationToken);

            if (row == null)
            {
                row = new DeviceDesiredState
                {
                    Target = target,
                    DesiredState = desiredState,
                    DesiredStateAt = now
                };
                _context.DeviceDesiredStates.Add(row);
            }
            else
            {
                row.DesiredState = desiredState;
                row.DesiredStateAt = now;
            }

            // A fresh intent starts its own retry budget, and is unsettled until the device is
            // seen to agree, even if it happens to already be in that state.
            row.Attempts = 0;
            row.Settled = false;

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Device desired state recorded {@LogData}", new { Target = target, DesiredState = desiredState });
            return row;
        }

        public async Task<DeviceDesiredState?> RecordObservedStateAsync(string target, string observedState, CancellationToken cancellationToken = default)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var row = await _context.DeviceDesiredStates.FirstOrDefaultAsync(d => d.Target == target, cancellationToken);
            if (row == null)
            {
                // Nothing asked for this device, so its state is just news, not progress.
                return null;
            }

            row.ObservedState = observedState;
            row.ObservedStateAt = now;
            row.Settled = string.Equals(row.DesiredState, observedState, StringComparison.OrdinalIgnoreCase);

            await _context.SaveChangesAsync(cancellationToken);
            return row;
        }

        public async Task<IReadOnlyList<DeviceDesiredState>> PendingAsync(CancellationToken cancellationToken = default) =>
            await _context.DeviceDesiredStates
                .Where(d => !d.Settled && d.Attempts < MaxAttempts)
                .OrderBy(d => d.DesiredStateAt)
                .ToListAsync(cancellationToken);

        public async Task<bool> RecordAttemptAsync(string target, CancellationToken cancellationToken = default)
        {
            var row = await _context.DeviceDesiredStates.FirstOrDefaultAsync(d => d.Target == target, cancellationToken);
            if (row == null)
            {
                return false;
            }

            row.Attempts++;
            await _context.SaveChangesAsync(cancellationToken);

            if (row.Attempts >= MaxAttempts && !row.Settled)
            {
                // Said once, loudly: an automation that asked for this needs to know it never
                // happened rather than find the device silently in the wrong state.
                _logger.LogWarning("Device command abandoned after {Attempts} attempts {@LogData}",
                    row.Attempts, new { row.Target, row.DesiredState, row.ObservedState });
            }

            return true;
        }
    }
}
