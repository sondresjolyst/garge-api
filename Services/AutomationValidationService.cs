using garge_api.Dtos.Automation;
using garge_api.Models;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    public interface IAutomationValidationService
    {
        Task<ValidationResult> ValidateCreateAutomationRuleAsync(CreateAutomationRuleDto dto);
        Task<ValidationResult> ValidateUpdateAutomationRuleAsync(UpdateAutomationRuleDto dto);
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public class AutomationValidationService : IAutomationValidationService
    {
        private readonly ApplicationDbContext _context;

        public AutomationValidationService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<ValidationResult> ValidateCreateAutomationRuleAsync(CreateAutomationRuleDto dto)
        {
            var errors = new List<string>();

            // Validate target type and ID
            if (string.IsNullOrWhiteSpace(dto.TargetType))
                errors.Add("TargetType is required.");
            
            if (dto.TargetId <= 0)
                errors.Add("TargetId must be greater than 0.");

            // Validate action
            if (string.IsNullOrWhiteSpace(dto.Action))
                errors.Add("Action is required.");
            else if (!new[] { "on", "off" }.Contains(dto.Action.ToLowerInvariant()))
                errors.Add("Action must be 'on' or 'off'.");

            // Validate conditions
            if (dto.Conditions == null || dto.Conditions.Count == 0)
            {
                errors.Add("At least one condition is required.");
            }
            else
            {
                // Validate logical operator for multiple conditions
                if (dto.Conditions.Count > 1)
                {
                    if (string.IsNullOrWhiteSpace(dto.LogicalOperator))
                    {
                        errors.Add("LogicalOperator is required when multiple conditions are specified.");
                    }
                    else if (!new[] { "AND", "OR" }.Contains(dto.LogicalOperator.ToUpperInvariant()))
                    {
                        errors.Add("LogicalOperator must be 'AND' or 'OR'.");
                    }
                }

                // Validate each condition
                foreach (var condition in dto.Conditions)
                {
                    ValidateSingleCondition(condition, errors);
                }
            }

            // Validate target exists
            if (!string.IsNullOrWhiteSpace(dto.TargetType) && dto.TargetId > 0)
            {
                var targetExists = await TargetExistsAsync(dto.TargetType, dto.TargetId);
                if (!targetExists)
                {
                    errors.Add($"Target {dto.TargetType} with ID {dto.TargetId} does not exist.");
                }
            }

            return new ValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors
            };
        }

        public async Task<ValidationResult> ValidateUpdateAutomationRuleAsync(UpdateAutomationRuleDto dto)
        {
            var errors = new List<string>();

            // Validate target type and ID
            if (string.IsNullOrWhiteSpace(dto.TargetType))
                errors.Add("TargetType is required.");
            
            if (dto.TargetId <= 0)
                errors.Add("TargetId must be greater than 0.");

            // Validate action
            if (string.IsNullOrWhiteSpace(dto.Action))
                errors.Add("Action is required.");
            else if (!new[] { "on", "off" }.Contains(dto.Action.ToLowerInvariant()))
                errors.Add("Action must be 'on' or 'off'.");

            // Validate conditions
            if (dto.Conditions == null || dto.Conditions.Count == 0)
            {
                errors.Add("At least one condition is required.");
            }
            else
            {
                // Validate logical operator for multiple conditions
                if (dto.Conditions.Count > 1)
                {
                    if (string.IsNullOrWhiteSpace(dto.LogicalOperator))
                    {
                        errors.Add("LogicalOperator is required when multiple conditions are specified.");
                    }
                    else if (!new[] { "AND", "OR" }.Contains(dto.LogicalOperator.ToUpperInvariant()))
                    {
                        errors.Add("LogicalOperator must be 'AND' or 'OR'.");
                    }
                }

                // Validate each condition
                foreach (var condition in dto.Conditions)
                {
                    ValidateSingleCondition(condition, errors);
                }
            }

            // Validate target exists
            if (!string.IsNullOrWhiteSpace(dto.TargetType) && dto.TargetId > 0)
            {
                var targetExists = await TargetExistsAsync(dto.TargetType, dto.TargetId);
                if (!targetExists)
                {
                    errors.Add($"Target {dto.TargetType} with ID {dto.TargetId} does not exist.");
                }
            }

            return new ValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors
            };
        }

        private void ValidateSingleCondition(AutomationConditionDto condition, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(condition.SensorType))
            {
                errors.Add("SensorType is required for each condition.");
            }

            // Allow -1 for electricity price sensor
            if (condition.SensorId < -1 || condition.SensorId == 0)
            {
                errors.Add("SensorId must be greater than 0 (or -1 for electricity price).");
            }

            if (string.IsNullOrWhiteSpace(condition.Condition))
            {
                errors.Add("Condition operator is required.");
            }
            else if (!new[] { "==", "=", ">", "<", ">=", "<=", "!=", "<>" }.Contains(condition.Condition))
            {
                errors.Add($"Invalid condition operator: {condition.Condition}. Allowed: ==, =, >, <, >=, <=, !=, <>");
            }

            // Threshold validation (can be any number including negative)
            // No additional validation needed as it's a double
        }

        private async Task<bool> TargetExistsAsync(string targetType, int targetId)
        {
            if (targetType.Equals("Switch", StringComparison.OrdinalIgnoreCase))
            {
                return await _context.Switches.AnyAsync(s => s.Id == targetId);
            }

            return false;
        }
    }
}
