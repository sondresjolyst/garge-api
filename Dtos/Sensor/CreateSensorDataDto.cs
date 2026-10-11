using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
	public class CreateSensorDataDto
	{
		// Value is stored in the IX_SensorData_SensorId_Timestamp index, and an index entry has a size limit
		[Required]
		[MaxLength(64)]
		public required string Value { get; set; }
	}
}
