using MapsterMapper;
using garge_api.Dtos.Subscription;
using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Subscription;
using garge_api.Services;
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
        private readonly IPriceChangeService _priceChanges;
        private readonly PriceChangeSweepService _sweep;
        private readonly IMapper _mapper;
        private readonly ILogger<ProductsController> _logger;

        public ProductsController(
            ApplicationDbContext context,
            IPriceChangeService priceChanges,
            PriceChangeSweepService sweep,
            IMapper mapper,
            ILogger<ProductsController> logger)
        {
            _context = context;
            _priceChanges = priceChanges;
            _sweep = sweep;
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

            var error = await _priceChanges.PrepareDirectEditAsync(product, dto.PriceInOre, dto.Interval);
            if (error != null) return Conflict(error);

            _mapper.Map(dto, product);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Product {ProductId} updated", id);
            return Ok(_mapper.Map<ProductResponseDto>(product));
        }

        /// <summary>Lists a product's price changes, newest first, with how far their notices got. Admin only.</summary>
        [HttpGet("{id}/price-changes")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> GetPriceChanges(int id)
        {
            if (!await _context.Products.AnyAsync(p => p.Id == id)) return NotFound();
            var changes = await _context.PriceChanges
                .Where(c => c.ProductId == id)
                .OrderByDescending(c => c.Id)
                .Select(c => new PriceChangeResponseDto
                {
                    Id = c.Id,
                    ProductId = c.ProductId,
                    OldPriceInOre = c.OldPriceInOre,
                    NewPriceInOre = c.NewPriceInOre,
                    EffectiveFrom = c.EffectiveFrom,
                    CreatedAt = c.CreatedAt,
                    CancelledAt = c.CancelledAt,
                    AppliedAt = c.AppliedAt,
                    NoticeCount = c.Notices.Count,
                    NotifiedCount = c.Notices.Count(n => n.NotifiedAt != null),
                    AppliedCount = c.Notices.Count(n => n.AppliedAt != null),
                })
                .ToListAsync();
            return Ok(changes);
        }

        /// <summary>
        /// Schedules a price increase. Subscribers are emailed a notice, and pay the new price from the
        /// effective date, or 30 days after their notice if that is later. Admin only.
        /// </summary>
        [HttpPost("{id}/price-changes")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> SchedulePriceChange(int id, [FromBody] SchedulePriceChangeDto dto)
        {
            if (!await _context.Products.AnyAsync(p => p.Id == id)) return NotFound();
            var (change, error) = await _priceChanges.ScheduleAsync(id, dto.NewPriceInOre, dto.EffectiveFrom, User.UserId());
            if (change == null) return BadRequest(error);
            _sweep.Trigger();
            return Ok(_mapper.Map<PriceChangeResponseDto>(change));
        }

        /// <summary>Cancels a scheduled price change before any subscriber pays the new price. Admin only.</summary>
        [HttpDelete("{id}/price-changes/{changeId}")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> CancelPriceChange(int id, int changeId)
        {
            if (!await _context.PriceChanges.AnyAsync(c => c.Id == changeId && c.ProductId == id)) return NotFound();
            var error = await _priceChanges.CancelAsync(changeId);
            return error == null ? NoContent() : Conflict(error);
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
