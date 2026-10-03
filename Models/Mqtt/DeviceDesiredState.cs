using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Models.Mqtt
{
    /// <summary>
    /// The state a target device is meant to be in, and the state it was last seen in. A command
    /// published to a device's <c>/set</c> topic is QoS 0 with no persistence, so one sent while
    /// no gateway holds the lease is simply lost. Recording the intent lets the operator keep
    /// reissuing it until the observed state matches, instead of firing once and hoping.
    /// </summary>
    [Index(nameof(Target), IsUnique = true)]
    public class DeviceDesiredState
    {
        [Key]
        public int Id { get; set; }

        /// <summary>The device the intent applies to, e.g. <c>wiz_SOCKET_6c2990a96cde</c>.</summary>
        [Required]
        [MaxLength(100)]
        public required string Target { get; set; }

        /// <summary>The wanted state, as published to <c>/set</c> (<c>ON</c> or <c>OFF</c>).</summary>
        [Required]
        [MaxLength(16)]
        public required string DesiredState { get; set; }

        public DateTime DesiredStateAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The last state reported by the controlling gateway, which takes it from the device's
        /// own <c>syncPilot</c> push rather than from anything it holds in memory. Null until the
        /// first report arrives.
        /// </summary>
        [MaxLength(16)]
        public string? ObservedState { get; set; }

        public DateTime? ObservedStateAt { get; set; }

        /// <summary>
        /// How many times the intent has been published without the observed state catching up.
        /// The operator gives up once this passes its limit so an automation sees a failure
        /// rather than a command that quietly never happened.
        /// </summary>
        public int Attempts { get; set; }

        /// <summary>True once the observed state matched the desired state.</summary>
        public bool Settled { get; set; }
    }
}
