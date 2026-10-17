using garge_api.Models;
using garge_api.Models.Shop;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    public static class OrderStock
    {
        /// <summary>Puts an order's items back on stock, without saving. Items with unlimited stock stay as they are.</summary>
        public static async Task RestoreAsync(ApplicationDbContext db, Order order, CancellationToken cancellationToken = default)
        {
            var ids = order.OrderItems.Select(oi => oi.ShopItemId).ToList();
            var items = await db.ShopItems.Where(i => ids.Contains(i.Id)).ToListAsync(cancellationToken);
            var byId = items.ToDictionary(i => i.Id);
            foreach (var oi in order.OrderItems)
            {
                if (byId.TryGetValue(oi.ShopItemId, out var item) && item.StockCount != -1)
                    item.StockCount += oi.Quantity;
            }
        }
    }
}
