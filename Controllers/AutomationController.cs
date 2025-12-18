using garge_api.Dtos.Automation;
using garge_api.Models;
using garge_api.Models.Automation;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;
using System.Security.Claims;
using AutoMapper;

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
        private readonly IAutomationValidationService _validationService;
        private readonly IMapper _mapper;

        public AutomationController(
            ApplicationDbContext context, 
            ILogger<AutomationController> logger,
            IAutomationValidationService validationService,
            IMapper mapper)
        {
            _context = context;
            _logger = logger;
            _validationService = validationService;
            _mapper = mapper;
        }

        private async Task<bool> UserHasAccessToAutomationAsync(AutomationRule rule)
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();

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
        [SwaggerResponse(400, "Bad request - validation errors.")]
        [SwaggerResponse(403, "Forbidden.")]
        public async Task<ActionResult<AutomationRuleDto>> CreateRule([FromBody] CreateAutomationRuleDto dto)
        {
            // Validate the input
            var validationResult = await _validationService.ValidateCreateAutomationRuleAsync(dto);
            if (!validationResult.IsValid)
            {
                return BadRequest(new { errors = validationResult.Errors });
            }

            // Create the automation rule
            var rule = new AutomationRule
            {
                TargetType = dto.TargetType,
                TargetId = dto.TargetId,
                Action = dto.Action,
                LogicalOperator = dto.LogicalOperator,
                Conditions = dto.Conditions.Select(c => new AutomationCondition
                {
                    SensorType = c.SensorType,
                    SensorId = c.SensorId,
                    Condition = c.Condition,
                    Threshold = c.Threshold
                }).ToList()
            };

            // Check access permissions
            if (!await UserHasAccessToAutomationAsync(rule))
            {
                return Forbid();
            }

            _context.AutomationRules.Add(rule);
            await _context.SaveChangesAsync();

            // Return the created rule with all related data
            var createdRule = await _context.AutomationRules
                .Include(ar => ar.Conditions)
                .FirstAsync(ar => ar.Id == rule.Id);

            var result = CreateAutomationRuleDto(createdRule);

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
            var allRules = await _context.AutomationRules
                .Include(ar => ar.Conditions)
                .ToListAsync();
            var accessibleRules = new List<AutomationRuleDto>();

            foreach (var rule in allRules)
            {
                if (await UserHasAccessToAutomationAsync(rule))
                {
                    accessibleRules.Add(CreateAutomationRuleDto(rule));
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
        [SwaggerResponse(400, "Bad request - validation errors.")]
        [SwaggerResponse(403, "Forbidden.")]
        [SwaggerResponse(404, "Rule not found.")]
        public async Task<ActionResult<AutomationRuleDto>> UpdateRule(int id, [FromBody] UpdateAutomationRuleDto dto)
        {
            var rule = await _context.AutomationRules
                .Include(ar => ar.Conditions)
                .FirstOrDefaultAsync(ar => ar.Id == id);
            
            if (rule == null)
                return NotFound();

            if (!await UserHasAccessToAutomationAsync(rule))
                return Forbid();

            // Validate the input
            var validationResult = await _validationService.ValidateUpdateAutomationRuleAsync(dto);
            if (!validationResult.IsValid)
            {
                return BadRequest(new { errors = validationResult.Errors });
            }

            // Check access to new target if changed
            if (rule.TargetType != dto.TargetType || rule.TargetId != dto.TargetId)
            {
                var hasTargetAccess = await UserHasAccessToTargetAsync(dto.TargetType, dto.TargetId);
                if (!hasTargetAccess)
                    return Forbid();
            }

            // Update the rule
            rule.TargetType = dto.TargetType;
            rule.TargetId = dto.TargetId;
            rule.LogicalOperator = dto.LogicalOperator;
            rule.Action = dto.Action;

            // Update conditions - remove existing ones and add new ones
            _context.AutomationConditions.RemoveRange(rule.Conditions);
            
            rule.Conditions = dto.Conditions.Select(c => new AutomationCondition
            {
                AutomationRuleId = rule.Id,
                SensorType = c.SensorType,
                SensorId = c.SensorId,
                Condition = c.Condition,
                Threshold = c.Threshold
            }).ToList();

            await _context.SaveChangesAsync();

            // Return the updated rule
            var updatedRule = await _context.AutomationRules
                .Include(ar => ar.Conditions)
                .FirstAsync(ar => ar.Id == rule.Id);

            var result = CreateAutomationRuleDto(updatedRule);

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
            var rule = await _context.AutomationRules
                .Include(ar => ar.Conditions)
                .FirstOrDefaultAsync(ar => ar.Id == id);
            
            if (rule == null)
                return NotFound();

            if (!await UserHasAccessToAutomationAsync(rule))
                return Forbid();

            _context.AutomationRules.Remove(rule);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        private AutomationRuleDto CreateAutomationRuleDto(AutomationRule rule)
        {
            return new AutomationRuleDto
            {
                Id = rule.Id,
                TargetType = rule.TargetType,
                TargetId = rule.TargetId,
                Action = rule.Action,
                LogicalOperator = rule.LogicalOperator,
                Conditions = rule.Conditions?.Select(c => new AutomationConditionDto
                {
                    Id = c.Id,
                    SensorType = c.SensorType,
                    SensorId = c.SensorId,
                    Condition = c.Condition,
                    Threshold = c.Threshold
                }).ToList() ?? new List<AutomationConditionDto>()
            };
        }

        private async Task<bool> UserHasAccessToTargetAsync(string targetType, int targetId)
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();

            if (userRoles.Contains("admin", StringComparer.OrdinalIgnoreCase) ||
                userRoles.Contains("automation_admin", StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            if (targetType.Equals("Switch", StringComparison.OrdinalIgnoreCase))
            {
                var targetSwitch = await _context.Switches.FindAsync(targetId);
                if (targetSwitch == null)
                    return false;

                var accessibleParentNames = await _context.Sensors
                    .Where(sensor => userRoles.Contains(sensor.Role))
                    .Select(sensor => sensor.ParentName)
                    .ToListAsync();

                var discovered = await _context.DiscoveredDevices
                    .AnyAsync(dd => accessibleParentNames.Contains(dd.DiscoveredBy) && dd.Target == targetSwitch.Name);

                return discovered;
            }

            return false;
        }
    }
}
