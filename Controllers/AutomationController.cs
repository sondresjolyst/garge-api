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
        private readonly ILogger<AutomationController> _logger;

        public AutomationController(ApplicationDbContext context, ILogger <AutomationController> logger)
        {
            _context = context;
            _logger = logger;
        }

        private async Task<bool> UserHasAccessToAutomationAsync(AutomationRule rule)
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();

            if (userRoles.Contains("admin", StringComparer.OrdinalIgnoreCase) ||
                userRoles.Contains("AutomationAdmin", StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Get accessible parent names from sensors the user owns
            var accessibleParentNames = await _context.UserSensors
                .Where(us => us.UserId == userId)
                .Join(_context.Sensors, us => us.SensorId, s => s.Id, (us, s) => s.ParentName)
                .ToListAsync();

            // Fetch the target name (switch)
            var targetSwitch = await _context.Switches.FindAsync(rule.TargetId);
            var targetName = targetSwitch?.Name;

            // Check if any device the user can access has discovered this target
            var discovered = targetName != null && await _context.DiscoveredDevices
                .AnyAsync(dd => accessibleParentNames.Contains(dd.DiscoveredBy) && dd.Target == targetName);

            return discovered;
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
                Action = dto.Action,
                IsEnabled = dto.IsEnabled,
                ElectricityPriceCondition = dto.ElectricityPriceCondition,
                ElectricityPriceThreshold = dto.ElectricityPriceThreshold,
                ElectricityPriceArea = dto.ElectricityPriceArea,
                ElectricityPriceOperator = dto.ElectricityPriceOperator,
                TimerDurationHours = dto.TimerDurationHours,
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
                Action = rule.Action,
                IsEnabled = rule.IsEnabled,
                LastTriggeredAt = rule.LastTriggeredAt,
                ElectricityPriceCondition = rule.ElectricityPriceCondition,
                ElectricityPriceThreshold = rule.ElectricityPriceThreshold,
                ElectricityPriceArea = rule.ElectricityPriceArea,
                ElectricityPriceOperator = rule.ElectricityPriceOperator,
                TimerDurationHours = rule.TimerDurationHours,
                TimerActivatedAt = rule.TimerActivatedAt,
            };

            return Ok(result);
        }

        /// <summary>
        /// Marks an automation rule as triggered (called by the operator).
        /// </summary>
        [HttpPatch("{id}/triggered")]
        [SwaggerOperation(Summary = "Records that an automation rule was triggered.")]
        [SwaggerResponse(204, "Updated successfully.")]
        [SwaggerResponse(404, "Rule not found.")]
        public async Task<IActionResult> MarkTriggered(int id)
        {
            var rule = await _context.AutomationRules.FindAsync(id);
            if (rule == null)
                return NotFound();

            rule.LastTriggeredAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return NoContent();
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
                        Action = rule.Action,
                        IsEnabled = rule.IsEnabled,
                        LastTriggeredAt = rule.LastTriggeredAt,
                        ElectricityPriceCondition = rule.ElectricityPriceCondition,
                        ElectricityPriceThreshold = rule.ElectricityPriceThreshold,
                        ElectricityPriceArea = rule.ElectricityPriceArea,
                        ElectricityPriceOperator = rule.ElectricityPriceOperator,
                        TimerDurationHours = rule.TimerDurationHours,
                        TimerActivatedAt = rule.TimerActivatedAt,
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
            rule.IsEnabled = dto.IsEnabled;
            rule.ElectricityPriceCondition = dto.ElectricityPriceCondition;
            rule.ElectricityPriceThreshold = dto.ElectricityPriceThreshold;
            rule.ElectricityPriceArea = dto.ElectricityPriceArea;
            rule.ElectricityPriceOperator = dto.ElectricityPriceOperator;
            rule.TimerDurationHours = dto.TimerDurationHours;

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
                Action = rule.Action,
                IsEnabled = rule.IsEnabled,
                LastTriggeredAt = rule.LastTriggeredAt,
                ElectricityPriceCondition = rule.ElectricityPriceCondition,
                ElectricityPriceThreshold = rule.ElectricityPriceThreshold,
                ElectricityPriceArea = rule.ElectricityPriceArea,
                ElectricityPriceOperator = rule.ElectricityPriceOperator,
                TimerDurationHours = rule.TimerDurationHours,
                TimerActivatedAt = rule.TimerActivatedAt,
            };

            return Ok(result);
        }

        /// <summary>
        /// Starts the timer on a timed automation rule (called by the operator).
        /// </summary>
        [HttpPatch("{id}/timer-start")]
        [SwaggerOperation(Summary = "Sets TimerActivatedAt to now for a timed rule.")]
        [SwaggerResponse(204, "Timer started.")]
        [SwaggerResponse(404, "Rule not found.")]
        public async Task<IActionResult> TimerStart(int id)
        {
            var rule = await _context.AutomationRules.FindAsync(id);
            if (rule == null)
                return NotFound();

            rule.TimerActivatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return NoContent();
        }

        /// <summary>
        /// Clears the timer on a timed automation rule (called by the operator when timer expires).
        /// </summary>
        [HttpPatch("{id}/timer-clear")]
        [SwaggerOperation(Summary = "Clears TimerActivatedAt, re-arming the rule.")]
        [SwaggerResponse(204, "Timer cleared.")]
        [SwaggerResponse(404, "Rule not found.")]
        public async Task<IActionResult> TimerClear(int id)
        {
            var rule = await _context.AutomationRules.FindAsync(id);
            if (rule == null)
                return NotFound();

            rule.TimerActivatedAt = null;
            await _context.SaveChangesAsync();
            return NoContent();
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
