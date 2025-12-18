# Multiple Conditions Automation Feature - Implementation Summary

## Overview
This implementation adds support for multiple conditions with logical operators (AND/OR) to the automation system, while maintaining full backward compatibility with existing single-condition rules.

## Database Changes

### New Entity: AutomationCondition
- **Id**: Primary key
- **AutomationRuleId**: Foreign key to AutomationRule
- **SensorType**: Type of sensor (e.g., "temperature", "humidity", "electricity_price")
- **SensorId**: ID of the sensor
- **Condition**: Operator ("==", ">", "<", ">=", "<=", "!=")
- **Threshold**: Threshold value for comparison

### Updated Entity: AutomationRule
- Made legacy fields nullable for backward compatibility:
  - **SensorType**: Now nullable
  - **SensorId**: Now nullable
  - **Condition**: Now nullable
  - **Threshold**: Now nullable
- Added new fields:
  - **LogicalOperator**: "AND" or "OR" (nullable)
- Added navigation property:
  - **Conditions**: Collection of AutomationCondition

## API Changes

### DTOs Updated
1. **AutomationConditionDto**: New DTO for individual conditions
2. **AutomationRuleDto**: Updated with backward compatibility
3. **CreateAutomationRuleDto**: Updated to support both formats
4. **UpdateAutomationRuleDto**: Updated to support both formats

### API Endpoints Enhanced
All automation endpoints now support both legacy single-condition and new multiple-condition formats:

#### POST /api/automation
**Legacy Format (still supported):**
```json
{
  "targetType": "switch",
  "targetId": 1,
  "sensorType": "temperature",
  "sensorId": 2,
  "condition": ">",
  "threshold": 10,
  "action": "on"
}
```

**New Multiple Conditions Format:**
```json
{
  "targetType": "switch",
  "targetId": 1,
  "conditions": [
    {
      "sensorType": "temperature",
      "sensorId": 2,
      "condition": ">",
      "threshold": 10
    },
    {
      "sensorType": "electricity_price",
      "sensorId": 3,
      "condition": "<=",
      "threshold": 3
    }
  ],
  "logicalOperator": "AND",
  "action": "on"
}
```

#### Response Format
All GET responses include both legacy fields and new conditions array:
```json
{
  "id": 1,
  "targetType": "switch",
  "targetId": 1,
  "conditions": [
    {
      "id": 1,
      "sensorType": "temperature",
      "sensorId": 2,
      "condition": ">",
      "threshold": 10
    }
  ],
  "logicalOperator": "AND",
  "action": "on",
  "sensorType": "temperature",
  "sensorId": 2,
  "condition": ">",
  "threshold": 10
}
```

## New Services

### 1. AutomationValidationService
- Validates automation rule DTOs
- Ensures at least one condition is specified
- Validates condition operators and logical operators
- Prevents mixing legacy and new formats

### 2. AutomationProcessingService
- Processes sensor data against automation rules
- Evaluates both single and multiple conditions
- Supports AND/OR logical operators
- Handles different sensor types (temperature, electricity_price, etc.)
- Executes actions (switch on/off)

### 3. SensorNotificationService
- Background service that listens to PostgreSQL notifications
- Triggers automation processing when new sensor data arrives
- Works alongside the existing PostgresNotificationService

## Database Triggers
Added PostgreSQL trigger for sensor data:
- Listens to `sensordata_channel`
- Automatically processes automation rules when new sensor data is inserted

## Backward Compatibility
✅ **Fully maintained:**
- Existing API calls work unchanged
- Legacy single-condition rules continue to function
- Database migration preserves existing data
- Response format includes both old and new fields

## Validation Rules
- At least one condition must be specified (either legacy or new format)
- Cannot mix legacy and new formats in the same request
- LogicalOperator required when multiple conditions are specified
- Valid condition operators: `==`, `=`, `>`, `<`, `>=`, `<=`, `!=`, `<>`
- Valid logical operators: `AND`, `OR`
- Valid actions: `on`, `off`

## Use Case Examples

### 1. Temperature AND Price
Turn on switch when temperature > 10°C AND electricity price ≤ 3 NOK/kWh

### 2. Humidity OR Temperature
Turn on switch when humidity < 30% OR temperature > 25°C

### 3. Complex Logic
Multiple conditions with different sensors and thresholds combined with logical operators

## Testing Recommendations

### 1. Backward Compatibility Tests
- Create automation rules using legacy format
- Verify existing rules still work after migration
- Test GET endpoints return both formats

### 2. New Feature Tests
- Create rules with multiple conditions
- Test AND logic (all conditions must be true)
- Test OR logic (any condition can be true)
- Test mixed sensor types

### 3. Validation Tests
- Test validation errors for invalid input
- Test mixing legacy and new formats (should fail)
- Test missing required fields

### 4. Processing Tests
- Insert sensor data and verify automation triggers
- Test edge cases (no data, invalid sensor values)
- Test different condition operators

## Configuration
New services automatically registered in Program.cs:
- IAutomationProcessingService
- IAutomationValidationService
- SensorNotificationService (background service)

## Performance Considerations
- Database triggers ensure real-time processing
- Efficient queries with proper indexing
- Background services don't block API requests
- Graceful error handling prevents system crashes
