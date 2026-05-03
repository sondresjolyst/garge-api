using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Push
{
    public class CreatePushSubscriptionDto
    {
        [Required]
        [MaxLength(2048)]
        public required string Endpoint { get; set; }

        [Required]
        [MaxLength(256)]
        public required string P256dh { get; set; }

        [Required]
        [MaxLength(64)]
        public required string Auth { get; set; }
    }

    public class DeletePushSubscriptionDto
    {
        [Required]
        public required string Endpoint { get; set; }
    }
}
