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
        private readonly ILogger<ElectricityController> _logger;

        public ElectricityController(NordPoolService nordPoolService, IMapper mapper, ILogger<ElectricityController> logger)
        {
            _nordPoolService = nordPoolService;
            _mapper = mapper;
            _logger = logger;
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
            _logger.LogInformation("GetPrices called by {@LogData}", new { User = User.Identity?.Name, type, area, date, currency });

            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();

            var hasAccess = userRoles.Contains("admin", StringComparer.OrdinalIgnoreCase) ||
                            userRoles.Any(role => RoleNames.RolePermissions.TryGetValue(role, out var permissions) && permissions.Contains("Electricity", StringComparer.OrdinalIgnoreCase));

            if (!hasAccess)
            {
                _logger.LogWarning("Access denied for user {@LogData}", new { User = User.Identity?.Name, Roles = string.Join(",", userRoles) });
                return Forbid();
            }

            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(area))
            {
                _logger.LogWarning("Bad request: Missing type or area {@LogData}", new { type, area });
                return BadRequest("Type and area parameters are required.");
            }

            var areas = new List<string> { area };
            _logger.LogInformation("Fetching prices from NordPoolService {@LogData}", new { type, area, date, currency });

            var data = await _nordPoolService.FetchPricesAsync(type.ToUpper(), date, areas, currency);

            if (data == null)
            {
                _logger.LogWarning("No price data found {@LogData}", new { type, area, date, currency });
                return NotFound(new { message = "No price data found." });
            }

            var dto = _mapper.Map<PriceResponseDto>(data);

            _logger.LogInformation("Returning price data {@LogData}", new { type, area, date, currency });
            return Ok(dto);
        }

        /// <summary>
        /// Retrieves the current electricity price for a specific area.
        /// </summary>
        /// <param name="area">The area code (default: NO2).</param>
        /// <param name="currency">The currency (default: NOK).</param>
        /// <returns>The current electricity price.</returns>
        [HttpGet("current-price")]
        public async Task<IActionResult> GetCurrentPrice([FromQuery] string area = "NO2", [FromQuery] string currency = "NOK")
        {
            _logger.LogInformation("GetCurrentPrice called by {@LogData}", new { User = User.Identity?.Name, area, currency });

            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();

            var hasAccess = userRoles.Contains("admin", StringComparer.OrdinalIgnoreCase) ||
                            userRoles.Any(role => RoleNames.RolePermissions.TryGetValue(role, out var permissions) && permissions.Contains("Electricity", StringComparer.OrdinalIgnoreCase));

            if (!hasAccess)
            {
                _logger.LogWarning("Access denied for user {@LogData}", new { User = User.Identity?.Name, Roles = string.Join(",", userRoles) });
                return Forbid();
            }

            _logger.LogInformation("Fetching current price from NordPoolService {@LogData}", new { area, currency });

            var currentPrice = await _nordPoolService.GetCurrentPriceAsync(area, currency);

            if (currentPrice == null)
            {
                _logger.LogWarning("No current price data found {@LogData}", new { area, currency });
                return NotFound(new { message = "No current price data found." });
            }

            var result = new
            {
                area,
                currency,
                price = currentPrice.Value,
                timestamp = DateTime.UtcNow
            };

            _logger.LogInformation("Returning current price data {@LogData}", new { area, currency, price = currentPrice.Value });
            return Ok(result);
        }
    }
}
