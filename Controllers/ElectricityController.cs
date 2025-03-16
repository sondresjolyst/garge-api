using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Linq;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class ElectricityController : ControllerBase
    {
        private readonly NordPoolService _nordPoolService;

        public ElectricityController(NordPoolService nordPoolService)
        {
            _nordPoolService = nordPoolService;
        }

        [HttpGet("prices")]
        public async Task<IActionResult> GetPrices([FromQuery] string type, [FromQuery] string area, [FromQuery] DateTime? date, [FromQuery] string currency = "NOK")
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();

            if (!userRoles.Contains("admin", StringComparer.OrdinalIgnoreCase) && !userRoles.Contains("Electricity", StringComparer.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(area))
            {
                return BadRequest("Type and area parameters are required.");
            }

            var areas = new List<string> { area };
            var data = await _nordPoolService.FetchPricesAsync(type.ToUpper(), date, areas, currency);
            return Ok(data);
        }
    }
}
