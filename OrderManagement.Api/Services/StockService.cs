using Microsoft.EntityFrameworkCore;
using OrderManagement.Api.Infrastructure.Persistence;

namespace OrderManagement.Api.Services;

public class StockService
{
    private readonly AppDbContext _db;
    public StockService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> DeductStockAsync(int productId, int quantity, CancellationToken cancellationToken = default) {
        var affectedRows = await _db.Products
            .Where(p =>
                p.Id == productId &&
                p.StockQuantity >= quantity)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    p => p.StockQuantity,
                    p => p.StockQuantity - quantity),
                cancellationToken);

        return affectedRows == 1;
    }

    public async Task RestoreStockAsync(int productId, int quantity, CancellationToken cancellationToken = default)
    {
        await _db.Products
            .Where(p => p.Id == productId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    p => p.StockQuantity,
                    p => p.StockQuantity + quantity),
                cancellationToken);
    }
}
