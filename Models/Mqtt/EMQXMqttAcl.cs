using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Models.Mqtt
{
    [Index(nameof(Username), nameof(Permission), nameof(Action), nameof(Topic), nameof(Qos), nameof(Retain), IsUnique = true)]
    public class EMQXMqttAcl
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        [Required]
        public string Username { get; set; } = null!;
        [Required]
        public string Permission { get; set; } = null!;
        [Required]
        public string Action { get; set; } = null!;
        [Required]
        public string Topic { get; set; } = null!;
        public short? Qos { get; set; }
        public short? Retain { get; set; }
    }
}
