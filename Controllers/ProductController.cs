using garge_api.Dtos.Product;
using garge_api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;
using AutoMapper;
using System.Security.Claims;
using garge_api.Models.Sensor;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/products")]
    [EnableCors("AllowAllOrigins")]
    [Authorize]
    public class ProductController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IMapper _mapper;
        private static readonly List<string> AdminRoles = new() { "product_admin", "admin" };

        public ProductController(ApplicationDbContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        private bool UserHasRequiredRole()
        {
            var userRoles = User.FindAll(ClaimTypes.Role).Select(r => r.Value).ToList();
            return userRoles.Any(role => AdminRoles.Contains(role, StringComparer.OrdinalIgnoreCase));
        }

        [HttpGet]
        [SwaggerOperation(Summary = "Retrieves all products.")]
        [SwaggerResponse(200, "A list of all products.", typeof(IEnumerable<ProductDto>))]
        public async Task<IActionResult> GetAllProducts()
        {
            var products = await _context.Products.ToListAsync();
            var dtos = _mapper.Map<IEnumerable<ProductDto>>(products);
            return Ok(dtos);
        }

        [HttpGet("{id}")]
        [SwaggerOperation(Summary = "Retrieves a product by its ID.")]
        [SwaggerResponse(200, "The product with the specified ID.", typeof(ProductDto))]
        [SwaggerResponse(404, "Product not found.")]
        public async Task<IActionResult> GetProduct(int id)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null)
                return NotFound(new { message = "Product not found!" });

            var dto = _mapper.Map<ProductDto>(product);
            return Ok(dto);
        }

        [HttpPost]
        [SwaggerOperation(Summary = "Creates a new product.")]
        [SwaggerResponse(201, "The created product.", typeof(ProductDto))]
        public async Task<IActionResult> CreateProduct([FromBody] CreateProductDto dto)
        {
            if (!UserHasRequiredRole())
                return Forbid();

            var product = _mapper.Map<Product>(dto);
            product.CreatedAt = DateTime.UtcNow;
            product.UpdatedAt = DateTime.UtcNow;

            _context.Products.Add(product);
            await _context.SaveChangesAsync();

            var resultDto = _mapper.Map<ProductDto>(product);
            return CreatedAtAction(nameof(GetProduct), new { id = product.Id }, resultDto);
        }

        [HttpPut("{id}")]
        [SwaggerOperation(Summary = "Updates an existing product.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Product not found.")]
        public async Task<IActionResult> UpdateProduct(int id, [FromBody] UpdateProductDto dto)
        {
            if (!UserHasRequiredRole())
                return Forbid();

            var product = await _context.Products.FindAsync(id);
            if (product == null)
                return NotFound(new { message = "Product not found!" });

            _mapper.Map(dto, product);
            product.UpdatedAt = DateTime.UtcNow;

            _context.Entry(product).State = EntityState.Modified;
            await _context.SaveChangesAsync();

            return NoContent();
        }

        [HttpDelete("{id}")]
        [SwaggerOperation(Summary = "Deletes a product by its ID.")]
        [SwaggerResponse(204, "No content.")]
        [SwaggerResponse(404, "Product not found.")]
        public async Task<IActionResult> DeleteProduct(int id)
        {
            if (!UserHasRequiredRole())
                return Forbid();

            var product = await _context.Products.FindAsync(id);
            if (product == null)
                return NotFound(new { message = "Product not found!" });

            _context.Products.Remove(product);
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}
