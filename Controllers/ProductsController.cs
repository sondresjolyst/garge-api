using MapsterMapper;
using garge_api.Dtos.Subscription;
using garge_api.Models;
using garge_api.Models.Subscription;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/products")]
    public class ProductsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IMapper _mapper;
        private readonly ILogger<ProductsController> _logger;

        public ProductsController(
            ApplicationDbContext context,
            IMapper mapper,
            ILogger<ProductsController> logger)
        {
            _context = context;
            _mapper = mapper;
            _logger = logger;
        }

        /// <summary>Lists all active subscription products.</summary>
        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> GetProducts()
        {
            var products = await _context.Products
                .Where(p => p.IsActive)
                .OrderBy(p => p.PriceInOre)
                .ToListAsync();

            return Ok(_mapper.Map<List<ProductResponseDto>>(products));
        }

        /// <summary>Gets a subscription product by ID.</summary>
        [HttpGet("{id}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetProduct(int id)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null) return NotFound();
            return Ok(_mapper.Map<ProductResponseDto>(product));
        }

        /// <summary>Creates a new subscription product.</summary>
        [HttpPost]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> CreateProduct([FromBody] CreateProductDto dto)
        {
            var product = _mapper.Map<Product>(dto);
            _context.Products.Add(product);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Product {ProductId} created: {Name}", product.Id, product.Name);
            return CreatedAtAction(nameof(GetProduct), new { id = product.Id },
                _mapper.Map<ProductResponseDto>(product));
        }

        /// <summary>Updates an existing subscription product.</summary>
        [HttpPut("{id}")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> UpdateProduct(int id, [FromBody] UpdateProductDto dto)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null) return NotFound();

            _mapper.Map(dto, product);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Product {ProductId} updated", id);
            return Ok(_mapper.Map<ProductResponseDto>(product));
        }

        /// <summary>Soft-deletes a subscription product (sets IsActive = false).</summary>
        [HttpDelete("{id}")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> DeleteProduct(int id)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null) return NotFound();

            product.IsActive = false;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Product {ProductId} deactivated", id);
            return NoContent();
        }
    }
}
