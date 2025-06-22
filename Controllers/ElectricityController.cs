using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using garge_api.Constants;
using AutoMapper;
using garge_api.Dtos.Electricity;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/electricity")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class ElectricityController : ControllerBase
    {
        private readonly NordPoolService _nordPoolService;
        private readonly IMapper _mapper;

        public ElectricityController(NordPoolService nordPoolService, IMapper mapper)
        {
            _nordPoolService = nordPoolService;
            _mapper = mapper;
        }

        /// <summary>
        /// Retrieves electricity prices for a given type, area, and date.
        /// </summary>
        /// <param name="type">The type of price data (e.g., "spot").</param>
        /// <param name="area">The area code (e.g., "NO1").</param>
        /// <param name="date">The date for which to retrieve prices.</param>
        /// <param name="currency">The currency (default: NOK).</param>
        /// <returns>Electricity price data for the specified parameters.</returns>
        [HttpGet("prices")]
        public async Task<IActionResult> GetPrices([FromQuery] string type, [FromQuery] string area, [FromQuery] DateTime? date, [FromQuery] string currency = "NOK")
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();

            var hasAccess = userRoles.Contains("admin", StringComparer.OrdinalIgnoreCase) ||
                            userRoles.Any(role => RoleNames.RolePermissions.TryGetValue(role, out var permissions) && permissions.Contains("Electricity", StringComparer.OrdinalIgnoreCase));

            if (!hasAccess)
                return Forbid();

            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(area))
                return BadRequest("Type and area parameters are required.");

            var areas = new List<string> { area };
            var data = await _nordPoolService.FetchPricesAsync(type.ToUpper(), date, areas, currency);

            if (data == null)
                return NotFound(new { message = "No price data found." });

            var dto = _mapper.Map<PriceResponseDto>(data);

            return Ok(dto);
        }
    }
}
