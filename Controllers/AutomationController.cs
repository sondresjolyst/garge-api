using garge_api.Dtos.Automation;
using garge_api.Models;
using garge_api.Models.Automation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;
using System.Security.Claims;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/automation")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class AutomationController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public AutomationController(ApplicationDbContext context)
        {
            _context = context;
        }

        private async Task<bool> UserHasAccessToAutomationAsync(AutomationRule rule)
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();

            // Admins always have access
            if (userRoles.Contains("admin", StringComparer.OrdinalIgnoreCase) ||
                userRoles.Contains("automation_admin", StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            // Get accessible parent names from sensors the user has a role for
            var accessibleParentNames = await _context.Sensors
                .Where(sensor => userRoles.Contains(sensor.Role))
                .Select(sensor => sensor.ParentName)
                .ToListAsync();

            // Check if any discovered device links the user to the sensor or target
            var sensorDiscovered = await _context.DiscoveredDevices
                .AnyAsync(dd => accessibleParentNames.Contains(dd.DiscoveredBy) && dd.Target == rule.SensorId.ToString());

            var targetDiscovered = await _context.DiscoveredDevices
                .AnyAsync(dd => accessibleParentNames.Contains(dd.DiscoveredBy) && dd.Target == rule.TargetId.ToString());

            return sensorDiscovered && targetDiscovered;
        }

        /// <summary>
        /// Creates a new automation rule.
        /// </summary>
        /// <param name="dto">The automation rule data.</param>
        /// <returns>The created rule.</returns>
        [HttpPost]
        [SwaggerOperation(Summary = "Creates a new automation rule.")]
        [SwaggerResponse(200, "Rule created successfully.", typeof(AutomationRuleDto))]
        [SwaggerResponse(403, "Forbidden.")]
        public async Task<ActionResult<AutomationRuleDto>> CreateRule([FromBody] CreateAutomationRuleDto dto)
        {
            var rule = new AutomationRule
            {
                TargetType = dto.TargetType,
                TargetId = dto.TargetId,
                SensorType = dto.SensorType,
                SensorId = dto.SensorId,
                Condition = dto.Condition,
                Threshold = dto.Threshold,
                Action = dto.Action
            };

            if (!await UserHasAccessToAutomationAsync(rule))
            {
                return Forbid();
            }

            _context.AutomationRules.Add(rule);
            await _context.SaveChangesAsync();

            var result = new AutomationRuleDto
            {
                Id = rule.Id,
                TargetType = rule.TargetType,
                TargetId = rule.TargetId,
                SensorType = rule.SensorType,
                SensorId = rule.SensorId,
                Condition = rule.Condition,
                Threshold = rule.Threshold,
                Action = rule.Action
            };

            return Ok(result);
        }

        /// <summary>
        /// Gets all automation rules.
        /// </summary>
        /// <returns>List of rules.</returns>
        [HttpGet]
        [SwaggerOperation(Summary = "Gets all automation rules the user has access to.")]
        [SwaggerResponse(200, "List of rules.", typeof(IEnumerable<AutomationRuleDto>))]
        public async Task<ActionResult<IEnumerable<AutomationRuleDto>>> GetRules()
        {
            var allRules = await _context.AutomationRules.ToListAsync();
            var accessibleRules = new List<AutomationRuleDto>();

            foreach (var rule in allRules)
            {
                if (await UserHasAccessToAutomationAsync(rule))
                {
                    accessibleRules.Add(new AutomationRuleDto
                    {
                        Id = rule.Id,
                        TargetType = rule.TargetType,
                        TargetId = rule.TargetId,
                        SensorType = rule.SensorType,
                        SensorId = rule.SensorId,
                        Condition = rule.Condition,
                        Threshold = rule.Threshold,
                        Action = rule.Action
                    });
                }
            }

            return Ok(accessibleRules);
        }

        /// <summary>
        /// Updates an existing automation rule.
        /// </summary>
        /// <param name="id">The rule ID.</param>
        /// <param name="dto">The updated rule data.</param>
        /// <returns>The updated rule.</returns>
        [HttpPut("{id}")]
        [SwaggerOperation(Summary = "Updates an existing automation rule.")]
        [SwaggerResponse(200, "Rule updated successfully.", typeof(AutomationRuleDto))]
        [SwaggerResponse(403, "Forbidden.")]
        [SwaggerResponse(404, "Rule not found.")]
        public async Task<ActionResult<AutomationRuleDto>> UpdateRule(int id, [FromBody] UpdateAutomationRuleDto dto)
        {
            var rule = await _context.AutomationRules.FindAsync(id);
            if (rule == null)
                return NotFound();

            if (!await UserHasAccessToAutomationAsync(rule))
                return Forbid();

            var tempRule = new AutomationRule
            {
                TargetType = dto.TargetType,
                TargetId = dto.TargetId,
                SensorType = dto.SensorType,
                SensorId = dto.SensorId,
                Condition = dto.Condition,
                Threshold = dto.Threshold,
                Action = dto.Action
            };
            if (!await UserHasAccessToAutomationAsync(tempRule))
                return Forbid();

            rule.TargetType = dto.TargetType;
            rule.TargetId = dto.TargetId;
            rule.SensorType = dto.SensorType;
            rule.SensorId = dto.SensorId;
            rule.Condition = dto.Condition;
            rule.Threshold = dto.Threshold;
            rule.Action = dto.Action;

            await _context.SaveChangesAsync();

            var result = new AutomationRuleDto
            {
                Id = rule.Id,
                TargetType = rule.TargetType,
                TargetId = rule.TargetId,
                SensorType = rule.SensorType,
                SensorId = rule.SensorId,
                Condition = rule.Condition,
                Threshold = rule.Threshold,
                Action = rule.Action
            };

            return Ok(result);
        }

        /// <summary>
        /// Deletes an automation rule.
        /// </summary>
        /// <param name="id">The rule ID.</param>
        /// <returns>No content.</returns>
        [HttpDelete("{id}")]
        [SwaggerOperation(Summary = "Deletes an automation rule.")]
        [SwaggerResponse(204, "Rule deleted successfully.")]
        [SwaggerResponse(403, "Forbidden.")]
        [SwaggerResponse(404, "Rule not found.")]
        public async Task<IActionResult> DeleteRule(int id)
        {
            var rule = await _context.AutomationRules.FindAsync(id);
            if (rule == null)
                return NotFound();

            if (!await UserHasAccessToAutomationAsync(rule))
                return Forbid();

            _context.AutomationRules.Remove(rule);
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}
