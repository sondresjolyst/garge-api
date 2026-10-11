using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
	public class CreateSensorDataDto
	{
		// Value is stored in the IX_SensorData_SensorId_Timestamp_Value index. An index entry must stay under
		// about 2.7 kB, and 512 characters is at most 2 kB of UTF-8.
		[Required]
		[MaxLength(512)]
		public required string Value { get; set; }
	}
}
