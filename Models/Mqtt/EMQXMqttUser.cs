using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Models.Mqtt
{
    [Index(nameof(Username), IsUnique = true)]
    public class EMQXMqttUser
    {
        [Key]
        public int Id { get; set; }
        public bool? IsSuperuser { get; set; }
        [MaxLength(100)]
        public string? Username { get; set; }
        [MaxLength(100)]
        public string? PasswordHash { get; set; }
        [MaxLength(40)]
        public string? Salt { get; set; }
    }
}
