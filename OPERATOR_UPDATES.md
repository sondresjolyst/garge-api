# Operator Updates Required for Multiple Conditions

Your operator code needs to be updated to handle the new multiple conditions format. Here are the changes needed:

## 1. Update AutomationRuleDto in your operator

```csharp
// In your operator project's Dtos/Automation/AutomationRuleDto.cs
public class AutomationRuleDto
{
    public int Id { get; set; }
    public required string TargetType { get; set; }
    public int TargetId { get; set; }
    
    // Legacy fields for backward compatibility
    public string? SensorType { get; set; }
    public int? SensorId { get; set; }
    public string? Condition { get; set; }
    public double? Threshold { get; set; }
    
    // New fields for multiple conditions
    public List<AutomationConditionDto>? Conditions { get; set; }
    public string? LogicalOperator { get; set; } // "AND" or "OR"
    
    public required string Action { get; set; }
}

public class AutomationConditionDto
{
    public int? Id { get; set; }
    public required string SensorType { get; set; }
    public int SensorId { get; set; }
    public required string Condition { get; set; }
    public double Threshold { get; set; }
}
```

## 2. Update the PollAutomationsAsync method

Replace the single condition evaluation with multiple conditions support:

```csharp
private async Task PollAutomationsAsync(CancellationToken stoppingToken)
{
    // ... existing code to get rules ...

    foreach (var rule in rules)
    {
        _logger.LogInformation("Automation Rule: {Rule}", JsonSerializer.Serialize(rule));

        if (!string.Equals(rule.TargetType, "Switch", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Skipping rule {RuleId} because TargetType is not 'Switch'.", rule.Id);
            continue;
        }

        // Evaluate conditions (both legacy and new format)
        bool conditionsMet = await EvaluateRuleConditionsAsync(rule, client, apiBaseUrl, stoppingToken);

        _logger.LogInformation("Rule {RuleId} conditionsMet={ConditionsMet}", rule.Id, conditionsMet);

        if (!conditionsMet)
        {
            _logger.LogInformation("Rule {RuleId} conditions not met. Skipping.", rule.Id);
            continue;
        }

        // ... rest of switch control logic remains the same ...
    }
}

private async Task<bool> EvaluateRuleConditionsAsync(AutomationRuleDto rule, HttpClient client, string apiBaseUrl, CancellationToken stoppingToken)
{
    // Handle legacy single condition format
    if (rule.Conditions == null || rule.Conditions.Count == 0)
    {
        if (rule.SensorId.HasValue && !string.IsNullOrEmpty(rule.SensorType))
        {
            return await EvaluateSingleConditionAsync(rule.SensorId.Value, rule.Condition!, rule.Threshold!.Value, client, apiBaseUrl, stoppingToken);
        }
        return false;
    }

    // Handle new multiple conditions format
    var conditionResults = new List<bool>();

    foreach (var condition in rule.Conditions)
    {
        bool conditionResult = await EvaluateSingleConditionAsync(
            condition.SensorId, 
            condition.Condition, 
            condition.Threshold, 
            client, 
            apiBaseUrl, 
            stoppingToken);
        
        conditionResults.Add(conditionResult);
        _logger.LogInformation("Condition for sensor {SensorId}: {Condition} {Threshold} = {Result}", 
            condition.SensorId, condition.Condition, condition.Threshold, conditionResult);
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

private async Task<bool> EvaluateSingleConditionAsync(int sensorId, string condition, double threshold, HttpClient client, string apiBaseUrl, CancellationToken stoppingToken)
{
    // Get latest sensor value
    var sensorResponse = await client.GetAsync($"{apiBaseUrl}/api/sensors/{sensorId}/latest-data", stoppingToken);
    if (!sensorResponse.IsSuccessStatusCode)
    {
        _logger.LogWarning("Failed to fetch latest data for sensor {SensorId}. Status: {StatusCode}", sensorId, sensorResponse.StatusCode);
        return false;
    }

    var sensorJson = await sensorResponse.Content.ReadAsStringAsync(stoppingToken);
    var sensorData = JsonDocument.Parse(sensorJson).RootElement;
    var valueStr = sensorData.GetProperty("value").GetString();
    
    if (!double.TryParse(valueStr, out var sensorValue))
    {
        _logger.LogWarning("Could not parse sensor value '{Value}' for sensor {SensorId}.", valueStr, sensorId);
        return false;
    }

    _logger.LogInformation("Evaluating condition: sensorValue={SensorValue}, condition={Condition}, threshold={Threshold}", sensorValue, condition, threshold);

    // Evaluate condition
    return condition switch
    {
        "<" => sensorValue < threshold,
        ">" => sensorValue > threshold,
        "<=" => sensorValue <= threshold,
        ">=" => sensorValue >= threshold,
        "==" or "=" => Math.Abs(sensorValue - threshold) < 0.001,
        "!=" or "<>" => Math.Abs(sensorValue - threshold) >= 0.001,
        _ => false
    };
}
```

## ⚡️ NEW: Electricity Price Sensor Support

The API now supports **Electricity Price** as a special sensor for automation! 

### Key Features:
- **Special Sensor ID**: `-1` (reserved for electricity price)
- **Sensor Type**: `electricity_price`
- **Real-time Data**: Current Norwegian electricity prices (NOK/kWh)
- **API Endpoint**: `/api/sensors/-1/latest-data` returns current price

### Example Usage:

**Legacy Format Rule:**
```json
{
  "targetType": "Switch",
  "targetId": 1,
  "sensorType": "electricity_price",
  "sensorId": -1,
  "condition": ">",
  "threshold": 3.0,
  "action": "Off"
}
```

**Multiple Conditions Format:**
```json
{
  "targetType": "Switch", 
  "targetId": 1,
  "conditions": [
    {
      "sensorType": "electricity_price",
      "sensorId": -1,
      "condition": ">",
      "threshold": 3.0
    },
    {
      "sensorType": "temperature",
      "sensorId": 5,
      "condition": "<",
      "threshold": 18.0
    }
  ],
  "logicalOperator": "AND",
  "action": "Off"
}
```

This turns off a switch when electricity price is above 3.0 NOK/kWh AND temperature is below 18°C.

Your operator will now support both regular sensors and electricity price-based automation rules!
```

This way your operator will support both:
1. **Legacy single condition rules** (backward compatibility)
2. **New multiple condition rules** with AND/OR logic

The API is now ready for both formats, and your operator just needs these updates to handle multiple conditions!
