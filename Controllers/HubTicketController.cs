using garge_api.Dtos.Hub;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace garge_api.Controllers
{
    /// <summary>
    /// Issues tickets for connecting to the device hub.
    /// </summary>
    [ApiController]
    [Route("api/hub-tickets")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class HubTicketController(IHubTicketService tickets) : ControllerBase
    {
        /// <summary>
        /// Issues a short-lived ticket for the signed-in user. Send it as the access token when connecting to
        /// /hubs/devices. It works for one connection and expires after a minute.
        /// </summary>
        [HttpPost]
        [ProducesResponseType(typeof(HubTicketResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<HubTicketResponseDto> Create()
        {
            return Ok(new HubTicketResponseDto
            {
                Ticket = tickets.Issue(User),
                ExpiresIn = (int)HubTicketService.Lifetime.TotalSeconds,
            });
        }
    }
}
