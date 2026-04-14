using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using garge_api.Constants;
using AutoMapper;
using garge_api.Dtos.Electricity;
using garge_api.Models;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

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
        private readonly ApplicationDbContext _context;

        private static string Sanitize(string input) => input.Replace("\r", "", StringComparison.Ordinal)
                                                              .Replace("\n", "", StringComparison.Ordinal);

        public ElectricityController(NordPoolService nordPoolService, IMapper mapper, ILogger<ElectricityController> logger, ApplicationDbContext context)
        {
            _nordPoolService = nordPoolService;
            _mapper = mapper;
            _logger = logger;
            _context = context;
        }

        /// <summary>
        /// Retrieves electricity prices for a given type, area, and date. Values are in kr/kWh.
        /// </summary>
        /// <param name="type">The resolution: HOURLY, DAILY, or MONTHLY.</param>
        /// <param name="area">The area code (e.g., "NO1").</param>
        /// <param name="date">The date for which to retrieve prices.</param>
        /// <param name="currency">The currency (default: NOK).</param>
        /// <returns>Electricity price data in kr/kWh for the specified parameters.</returns>
        [HttpGet("prices")]
        public async Task<IActionResult> GetPrices([FromQuery] string type, [FromQuery] string area, [FromQuery] DateTime? date, [FromQuery] string currency = "NOK")
        {
            _logger.LogInformation("GetPrices called by {@LogData}", new { User = User.Identity?.Name, type = Sanitize(type), area = Sanitize(area), date, currency });

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

            var resolution = type.ToUpper();
            var queryDate = date ?? DateTime.UtcNow;

            // Try serving from DB first
            var storedEntries = await GetStoredPricesAsync(resolution, area, queryDate);
            if (storedEntries.Count > 0)
            {
                _logger.LogInformation("Serving {Count} {Resolution} price entries from DB for area {Area}", storedEntries.Count, Sanitize(resolution), Sanitize(area));
                var dbDto = BuildPriceResponseDto(storedEntries, area, currency);
                return Ok(dbDto);
            }

            // Fall back to NordPool
            _logger.LogInformation("No DB data found, fetching from NordPool {@LogData}", new { type = Sanitize(type), area = Sanitize(area), date, currency });
            var data = await _nordPoolService.FetchPricesAsync(resolution, queryDate, new List<string> { area }, currency);

            if (data == null)
            {
                _logger.LogWarning("No price data found {@LogData}", new { type, area, date, currency });
                return NotFound(new { message = "No price data found." });
            }

            // Convert NordPool values (NOK/MWh) to kr/kWh before returning
            foreach (var areaPrices in data.Areas.Values)
                foreach (var entry in areaPrices.Values)
                    entry.Value /= 1000m;

            var dto = _mapper.Map<PriceResponseDto>(data);
            _logger.LogInformation("Returning NordPool price data {@LogData}", new { type = Sanitize(type), area = Sanitize(area), date, currency });
            return Ok(dto);
        }

        private async Task<List<garge_api.Models.Electricity.StoredElectricityPrice>> GetStoredPricesAsync(string resolution, string area, DateTime date)
        {
            var utcDate = date.ToUniversalTime();

            if (resolution == "HOURLY")
            {
                var dayStart = new DateTime(utcDate.Year, utcDate.Month, utcDate.Day, 0, 0, 0, DateTimeKind.Utc);
                var dayEnd = dayStart.AddDays(1);
                return await _context.StoredElectricityPrices
                    .Where(p => p.Area == area && p.Resolution == "HOURLY" && p.DeliveryStart >= dayStart && p.DeliveryStart < dayEnd)
                    .OrderBy(p => p.DeliveryStart)
                    .ToListAsync();
            }
            else
            {
                var year = utcDate.Year;
                return await _context.StoredElectricityPrices
                    .Where(p => p.Area == area && p.Resolution == resolution && p.DeliveryStart.Year == year)
                    .OrderBy(p => p.DeliveryStart)
                    .ToListAsync();
            }
        }

        private static PriceResponseDto BuildPriceResponseDto(
            List<garge_api.Models.Electricity.StoredElectricityPrice> entries, string area, string currency)
        {
            var areaPricesDto = new AreaPricesDto
            {
                Values = entries.Select(e => new PriceEntryDto
                {
                    Start = e.DeliveryStart,
                    End = e.DeliveryEnd,
                    Value = (decimal)e.Value,  // already in kr/kWh
                }).ToList()
            };

            return new PriceResponseDto
            {
                Start = entries.First().DeliveryStart,
                End = entries.Last().DeliveryEnd,
                Updated = entries.Max(e => e.FetchedAt),
                Currency = currency,
                Areas = new Dictionary<string, AreaPricesDto> { [area] = areaPricesDto }
            };
        }

        /// <summary>
        /// Returns the current hour's electricity price for the given area from stored data.
        /// </summary>
        /// <param name="area">The NordPool area code (e.g. "NO2").</param>
        /// <returns>Current price entry in kr/kWh.</returns>
        [HttpGet("current-price")]
        [SwaggerOperation(Summary = "Gets the current hour's stored electricity price for an area.")]
        [SwaggerResponse(200, "Current price entry.")]
        [SwaggerResponse(404, "No price data found for current hour.")]
        public async Task<IActionResult> GetCurrentPrice([FromQuery] string area)
        {
            if (string.IsNullOrEmpty(area))
                return BadRequest("Area parameter is required.");

            var now = DateTime.UtcNow;
            var entry = await _context.StoredElectricityPrices
                .Where(p => p.Area == area && p.Resolution == "HOURLY" && p.DeliveryStart <= now && p.DeliveryEnd > now)
                .OrderByDescending(p => p.FetchedAt)
                .FirstOrDefaultAsync();

            if (entry == null)
                return NotFound(new { message = $"No stored price found for area '{area}' at current time." });

            return Ok(new
            {
                area = entry.Area,
                value = entry.Value,
                currency = entry.Currency,
                deliveryStart = entry.DeliveryStart,
                deliveryEnd = entry.DeliveryEnd,
            });
        }
    }
}
