// This service is no longer used - automation processing is handled by the operator
// Keeping file for reference but commented out

/*
using garge_api.Models;
using garge_api.Models.Automation;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Globalization;

namespace garge_api.Services
{
    public interface IAutomationProcessingService
    {
        Task ProcessSensorDataAsync(SensorData sensorData);
    }

    public class AutomationProcessingService : IAutomationProcessingService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AutomationProcessingService> _logger;

        public AutomationProcessingService(IServiceScopeFactory scopeFactory, ILogger<AutomationProcessingService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task ProcessSensorDataAsync(SensorData sensorData)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Get all automation rules that might be triggered by this sensor
                var relevantRules = await GetRelevantAutomationRulesAsync(context, sensorData);

                foreach (var rule in relevantRules)
                {
                    if (await EvaluateRuleAsync(context, rule, sensorData))
                    {
                        await ExecuteActionAsync(context, rule);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing sensor data for automation rules. SensorId: {SensorId}", sensorData.SensorId);
            }
        }

        private async Task<List<AutomationRule>> GetRelevantAutomationRulesAsync(ApplicationDbContext context, SensorData sensorData)
        {
            var sensor = await context.Sensors.FindAsync(sensorData.SensorId);
            if (sensor == null) return new List<AutomationRule>();

            // Get rules that have conditions involving this sensor
            var rules = await context.AutomationRules
                .Include(ar => ar.Conditions)
                .Where(ar => 
                    // New multi-condition rules
                    ar.Conditions.Any(c => c.SensorId == sensorData.SensorId && c.SensorType == sensor.Type) ||
                    // Legacy single-condition rules
                    (ar.SensorId == sensorData.SensorId && ar.SensorType == sensor.Type)
                )
                .ToListAsync();

            return rules;
        }

        private async Task<bool> EvaluateRuleAsync(ApplicationDbContext context, AutomationRule rule, SensorData triggeringSensorData)
        {
            // Handle legacy single-condition rules
            if (rule.Conditions.Count == 0 && rule.SensorId.HasValue && !string.IsNullOrEmpty(rule.SensorType))
            {
                return await EvaluateSingleConditionAsync(
                    triggeringSensorData,
                    rule.SensorId.Value,
                    rule.SensorType,
                    rule.Condition!,
                    rule.Threshold!.Value);
            }

            // Handle new multi-condition rules
            if (rule.Conditions.Count > 0)
            {
                var conditionResults = new List<bool>();

                foreach (var condition in rule.Conditions)
                {
                    bool conditionResult;
                    
                    if (condition.SensorId == triggeringSensorData.SensorId)
                    {
                        // Use the triggering sensor data directly
                        conditionResult = await EvaluateSingleConditionAsync(
                            triggeringSensorData,
                            condition.SensorId,
                            condition.SensorType,
                            condition.Condition,
                            condition.Threshold);
                    }
                    else
                    {
                        // Get the latest data for other sensors
                        var sensorData = await context.SensorData
                            .Where(sd => sd.SensorId == condition.SensorId)
                            .OrderByDescending(sd => sd.Timestamp)
                            .FirstOrDefaultAsync();

                        if (sensorData == null)
                        {
                            conditionResult = false; // No data available for this sensor
                        }
                        else
                        {
                            conditionResult = await EvaluateSingleConditionAsync(
                                sensorData,
                                condition.SensorId,
                                condition.SensorType,
                                condition.Condition,
                                condition.Threshold);
                        }
                    }

                    conditionResults.Add(conditionResult);
                }

                // Apply logical operator
                if (rule.LogicalOperator == "OR")
                {
                    return conditionResults.Any(r => r);
                }
                else // Default to AND
                {
                    return conditionResults.All(r => r);
                }
            }

            return false;
        }

        private async Task<bool> EvaluateSingleConditionAsync(SensorData sensorData, int sensorId, string sensorType, string condition, double threshold)
        {
            try
            {
                // Handle different sensor types
                double sensorValue;
                
                if (sensorType.ToLower() == "electricity_price")
                {
                    // For electricity price, the value might be in a JSON format
                    if (TryParseElectricityPrice(sensorData.Value, out var price))
                    {
                        sensorValue = price;
                    }
                    else
                    {
                        _logger.LogWarning("Failed to parse electricity price from sensor data: {Value}", sensorData.Value);
                        return false;
                    }
                }
                else
                {
                    // For other sensor types, try to parse as double
                    if (!double.TryParse(sensorData.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out sensorValue))
                    {
                        _logger.LogWarning("Failed to parse sensor value as double: {Value}", sensorData.Value);
                        return false;
                    }
                }

                // Evaluate the condition
                return condition.ToLower() switch
                {
                    "==" or "=" => Math.Abs(sensorValue - threshold) < 0.001,
                    ">" => sensorValue > threshold,
                    "<" => sensorValue < threshold,
                    ">=" => sensorValue >= threshold,
                    "<=" => sensorValue <= threshold,
                    "!=" or "<>" => Math.Abs(sensorValue - threshold) >= 0.001,
                    _ => false
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating condition for sensor {SensorId}", sensorId);
                return false;
            }
        }

        private bool TryParseElectricityPrice(string value, out double price)
        {
            price = 0;
            
            try
            {
                // Try direct double parsing first
                if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out price))
                {
                    return true;
                }

                // Try JSON parsing for more complex structures
                var jsonObject = JsonConvert.DeserializeObject<dynamic>(value);
                if (jsonObject?.price != null)
                {
                    price = Convert.ToDouble(jsonObject.price);
                    return true;
                }
                if (jsonObject?.value != null)
                {
                    price = Convert.ToDouble(jsonObject.value);
                    return true;
                }
            }
            catch
            {
                // Ignore JSON parsing errors and fall back to false
            }

            return false;
        }

        private async Task ExecuteActionAsync(ApplicationDbContext context, AutomationRule rule)
        {
            try
            {
                _logger.LogInformation("Executing automation action: {Action} for {TargetType} {TargetId}", 
                    rule.Action, rule.TargetType, rule.TargetId);

                if (rule.TargetType.ToLower() == "switch")
                {
                    var targetSwitch = await context.Switches.FindAsync(rule.TargetId);
                    if (targetSwitch == null)
                    {
                        _logger.LogWarning("Target switch not found: {TargetId}", rule.TargetId);
                        return;
                    }

                    // Create switch data entry to trigger the action
                    var switchData = new SwitchData
                    {
                        SwitchId = rule.TargetId,
                        Value = rule.Action.ToLower() == "on" ? "1" : "0",
                        Timestamp = DateTime.UtcNow
                    };

                    context.SwitchData.Add(switchData);
                    await context.SaveChangesAsync();

                    _logger.LogInformation("Switch action executed: {SwitchName} turned {Action}", 
                        targetSwitch.Name, rule.Action);
                }
                else
                {
                    _logger.LogWarning("Unsupported target type: {TargetType}", rule.TargetType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing automation action for rule {RuleId}", rule.Id);
            }
        }
    }
}
*/
