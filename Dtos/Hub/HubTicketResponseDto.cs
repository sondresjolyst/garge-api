namespace garge_api.Dtos.Hub
{
    public class HubTicketResponseDto
    {
        /// <summary>The ticket to send as the access token when connecting to the device hub.</summary>
        public required string Ticket { get; set; }

        /// <summary>How many seconds the ticket can open a connection.</summary>
        public int ExpiresIn { get; set; }
    }
}
