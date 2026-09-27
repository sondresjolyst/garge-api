using garge_api.Constants;
using garge_api.Dtos.Operator;
using garge_api.Hubs;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/operator")]
    [Authorize(Roles = $"{RoleNames.Admin},{DeviceHub.BridgeRole}")]
    public class OperatorController : ControllerBase
    {
        private readonly IPipelineHealthService _pipeline;

        public OperatorController(IPipelineHealthService pipeline)
        {
            _pipeline = pipeline;
        }

        /// <summary>
        /// Records garge-operator's heartbeat. Sent every minute; a missing heartbeat or
        /// <c>mqttConnected: false</c> marks a pipeline outage, during which Garge Security alerts are held.
        /// </summary>
        [HttpPost("heartbeat")]
        [SwaggerOperation(Summary = "Records garge-operator's heartbeat.")]
        [SwaggerResponse(204, "Recorded.")]
        public async Task<IActionResult> Heartbeat([FromBody] OperatorHeartbeatDto dto, CancellationToken ct = default)
        {
            await _pipeline.RecordHeartbeatAsync(dto.MqttConnected, DateTime.UtcNow, ct);
            return NoContent();
        }
    }
}
