using garge_api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BCrypt.Net;
using Microsoft.AspNetCore.Cors;

namespace garge_api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CustomerController(ApplicationDbContext context) : ControllerBase
    {
        private readonly ApplicationDbContext _context = context;

        // GET: api/Customer
        [HttpGet]
        public ActionResult<IEnumerable<Customer>> GetCustomers()
        {
            return _context.Customer.ToList();
        }

        // GET: api/Customer/1
        [HttpGet("{id}")]
        public ActionResult<Customer> GetCustomer(int id)
        {
            var customer = _context.Customer.Find(id);
            if (customer == null)
            {
                return NotFound();
            }
            return customer;
        }

        // POST: api/Customer
        [HttpPost]
        public ActionResult<Customer> CreateCustomer(Customer customer)
        {
            if (customer == null)
            {
                return BadRequest();
            }
            _context.Customer.Add(customer);
            _context.SaveChanges();
            return CreatedAtAction(nameof(GetCustomer), new { id = customer.CustomerId }, customer);
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    [EnableCors("AllowAllOrigins")]
    public class AuthController(ApplicationDbContext context) : ControllerBase
    {
        private readonly ApplicationDbContext _context = context;

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] User user)
        {
            // Hash the password and store the user in the database
            user.Password = BCrypt.Net.BCrypt.HashPassword(user.Password);
            _context.Users.Add(user);
            await _context.SaveChangesAsync();
            return Ok(new { message = "User registered successfully!" });
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginModel login)
        {
            var user = await _context.Users.SingleOrDefaultAsync(u => u.Email == login.Email);
            if (user == null || !BCrypt.Net.BCrypt.Verify(login.Password, user.Password))
            {
                return Unauthorized(new { message = "Invalid credentials!" });
            }

            return Ok(new { message = "Login successful!" });
        }
    }
}
