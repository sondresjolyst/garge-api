using MapsterMapper;
using garge_api.Dtos.Admin;
using garge_api.Models;
using garge_api.Models.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/settings")]
    public class SettingsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IMapper _mapper;

        public SettingsController(ApplicationDbContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        /// <summary>Returns public-facing app settings including company info.</summary>
        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> GetPublicSettings()
        {
            var settings = await _context.AppSettings.FindAsync(1) ?? new AppSettings();
            return Ok(_mapper.Map<PublicSettingsDto>(settings));
        }
    }
}
