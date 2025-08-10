using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace garge_api.Models.Mqtt
{
    [Index(nameof(DiscoveredBy), nameof(Target), nameof(Type), IsUnique = true)]
    public class DiscoveredDevice
    {
        [Key]
        public int Id { get; set; }
        [Required]
        public string DiscoveredBy { get; set; } = null!;
        [Required]
        public string Target { get; set; } = null!;
        [Required]
        public string Type { get; set; } = null!;
        [Required]
        public DateTime Timestamp { get; set; }
    }
}
