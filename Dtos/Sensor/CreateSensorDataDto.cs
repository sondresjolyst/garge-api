using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
	public class CreateSensorDataDto
	{
		[Required]
		public required string Value { get; set; }
	}
}
