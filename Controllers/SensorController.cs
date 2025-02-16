using garge_api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;
using System.Security.Claims;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class SensorController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly RoleManager<IdentityRole> _roleManager;

        public SensorController(ApplicationDbContext context, RoleManager<IdentityRole> roleManager)
        {
            _context = context;
            _roleManager = roleManager;
        }

        private bool UserHasRequiredRole(string sensorRole)
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();
            return userRoles.Any(role => role.Equals(sensorRole, StringComparison.OrdinalIgnoreCase)) ||
                   userRoles.Any(role => role.Equals("sensor_admin", StringComparison.OrdinalIgnoreCase)) ||
                   userRoles.Any(role => role.Equals("admin", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Retrieves all sensors the user has access to.
        /// </summary>
        /// <returns>A list of sensors the user has access to.</returns>
        [HttpGet]
        [SwaggerOperation(Summary = "Retrieves all available sensors.")]
        [SwaggerResponse(200, "A list of all sensors.", typeof(IEnumerable<Sensor>))]
        public async Task<IActionResult> GetAllSensors()
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();

            // If the user has the "admin" or "sensor_admin" role, return all sensors
            if (UserHasRequiredRole("admin") || UserHasRequiredRole("sensor_admin"))
            {
                var allSensors = await _context.Sensors.ToListAsync();
                return Ok(allSensors);
            }

            // Otherwise, return only the sensors the user has access to
            var accessibleSensors = await _context.Sensors
                .Where(sensor => UserHasRequiredRole(sensor.Role))
                .ToListAsync();

            return Ok(accessibleSensors);
        }

        /// <summary>
        /// Retrieves a sensor by its ID.
        /// </summary>
        /// <param name="id">The ID of the sensor to retrieve.</param>
        /// <returns>The sensor with the specified ID.</returns>
        [HttpGet("{id}")]
        [SwaggerOperation(Summary = "Retrieves a sensor by its ID.")]
        [SwaggerResponse(200, "The sensor with the specified ID.", typeof(Sensor))]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetSensor(int id)
        {
            var sensor = await _context.Sensors.FindAsync(id);
            if (sensor == null)
            {
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!UserHasRequiredRole(sensor.Role))
            {
                return Forbid();
            }

            return Ok(sensor);
        }

        /// <summary>
        /// Retrieves data for a specific sensor.
        /// </summary>
        /// <param name="sensorId">The ID of the sensor to retrieve data for.</param>
        /// <returns>The data for the specified sensor.</returns>
        [HttpGet("{sensorId}/data")]
        [SwaggerOperation(Summary = "Retrieves data for a specific sensor.")]
        [SwaggerResponse(200, "The data for the specified sensor.", typeof(IEnumerable<SensorData>))]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> GetSensorData(int sensorId)
        {
            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
            {
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!UserHasRequiredRole(sensor.Role))
            {
                return Forbid();
            }

            var sensorData = await _context.SensorData
                .Where(sd => sd.SensorId == sensorId)
                .OrderBy(sd => sd.Timestamp) // Sort by Timestamp
                .ToListAsync();

            return Ok(sensorData);
        }

        /// <summary>
        /// Creates a new sensor.
        /// </summary>
        /// <param name="sensorDto">The sensor to create.</param>
        /// <returns>The created sensor.</returns>
        [HttpPost]
        [SwaggerOperation(Summary = "Creates a new sensor.")]
        [SwaggerResponse(201, "The created sensor.", typeof(Sensor))]
        [SwaggerResponse(409, "Sensor name already exists.")]
        public async Task<IActionResult> CreateSensor([FromBody] CreateSensorDto sensorDto)
        {
            if (!UserHasRequiredRole("sensor_admin"))
            {
                return Forbid();
            }

            // Ensure the Id is not set by the user
            var sensor = new Sensor
            {
                Name = sensorDto.Name,
                Type = sensorDto.Type,
                Role = sensorDto.Name
            };

            // Check if the role already exists
            if (!await _roleManager.RoleExistsAsync(sensor.Role))
            {
                var roleResult = await _roleManager.CreateAsync(new IdentityRole(sensor.Role));
                if (!roleResult.Succeeded)
                {
                    return StatusCode(500, new { message = "Failed to create role!" });
                }
            }

            _context.Sensors.Add(sensor);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate key") == true)
            {
                return Conflict(new { message = "Sensor name already exists!" });
            }

            return CreatedAtAction(nameof(GetSensor), new { id = sensor.Id }, sensor);
        }

        /// <summary>
        /// Creates new data for a specific sensor using sensorId.
        /// </summary>
        /// <param name="sensorId">The ID of the sensor to create data for.</param>
        /// <param name="sensorDataDto">The data to create.</param>
        /// <returns>The created sensor data.</returns>
        [HttpPost("{sensorId}/data")]
        [SwaggerOperation(Summary = "Creates new data for a specific sensor using sensorId.")]
        [SwaggerResponse(201, "The created sensor data.", typeof(SensorData))]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> CreateSensorDataById(int sensorId, [FromBody] CreateSensorDataDto sensorDataDto)
        {
            var sensor = await _context.Sensors.FindAsync(sensorId);
            if (sensor == null)
            {
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!UserHasRequiredRole(sensor.Role))
            {
                return Forbid();
            }

            var sensorData = new SensorData
            {
                SensorId = sensorId,
                Value = sensorDataDto.Value,
                Timestamp = DateTime.UtcNow
            };

            _context.SensorData.Add(sensorData);
            await _context.SaveChangesAsync();

            // Retrieve and return the sorted sensor data
            var sortedSensorData = await _context.SensorData
                .Where(sd => sd.SensorId == sensorId)
                .OrderBy(sd => sd.Timestamp)
                .ToListAsync();

            return CreatedAtAction(nameof(GetSensorData), new { sensorId = sensorId }, sortedSensorData);
        }

        /// <summary>
        /// Creates new data for a specific sensor using sensorName.
        /// </summary>
        /// <param name="sensorName">The name of the sensor to create data for.</param>
        /// <param name="sensorDataDto">The data to create.</param>
        /// <returns>The created sensor data.</returns>
        [HttpPost("name/{sensorName}/data")]
        [SwaggerOperation(Summary = "Creates new data for a specific sensor using sensorName.")]
        [SwaggerResponse(201, "The created sensor data.", typeof(SensorData))]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> CreateSensorDataByName(string sensorName, [FromBody] CreateSensorDataDto sensorDataDto)
        {
            var sensor = await _context.Sensors.FirstOrDefaultAsync(s => s.Name == sensorName);
            if (sensor == null)
            {
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!UserHasRequiredRole(sensor.Role))
            {
                return Forbid();
            }

            var sensorData = new SensorData
            {
                SensorId = sensor.Id,
                Value = sensorDataDto.Value,
                Timestamp = DateTime.UtcNow
            };

            _context.SensorData.Add(sensorData);
            await _context.SaveChangesAsync();

            // Retrieve and return the sorted sensor data
            var sortedSensorData = await _context.SensorData
                .Where(sd => sd.SensorId == sensor.Id)
                .OrderBy(sd => sd.Timestamp)
                .ToListAsync();

            return CreatedAtAction(nameof(GetSensorData), new { sensorId = sensor.Id }, sortedSensorData);
        }

        /// <summary>
        /// Updates an existing sensor.
        /// </summary>
        /// <param name="id">The ID of the sensor to update.</param>
        /// <param name="sensor">The updated sensor data.</param>
        /// <returns>No content.</returns>
        [HttpPut("{id}")]
        [SwaggerOperation(Summary = "Updates an existing sensor.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(400, "Bad request.")]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> UpdateSensor(int id, [FromBody] Sensor sensor)
        {
            if (id != sensor.Id)
            {
                return BadRequest();
            }

            var existingSensor = await _context.Sensors.FindAsync(id);
            if (existingSensor == null)
            {
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!UserHasRequiredRole(existingSensor.Role))
            {
                return Forbid();
            }

            existingSensor.Name = sensor.Name;
            existingSensor.Type = sensor.Type;
            existingSensor.Role = sensor.Role;

            _context.Entry(existingSensor).State = EntityState.Modified;
            await _context.SaveChangesAsync();

            return NoContent();
        }

        /// <summary>
        /// Deletes a sensor by its ID.
        /// </summary>
        /// <param name="id">The ID of the sensor to delete.</param>
        /// <returns>No content.</returns>
        [HttpDelete("{id}")]
        [SwaggerOperation(Summary = "Deletes a sensor by its ID.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Sensor not found.")]
        [SwaggerResponse(403, "User does not have the required role.")]
        public async Task<IActionResult> DeleteSensor(int id)
        {
            var sensor = await _context.Sensors.FindAsync(id);
            if (sensor == null)
            {
                return NotFound(new { message = "Sensor not found!" });
            }

            if (!UserHasRequiredRole(sensor.Role))
            {
                return Forbid();
            }

            _context.Sensors.Remove(sensor);
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}
