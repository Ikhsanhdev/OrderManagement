using Microsoft.EntityFrameworkCore;
using OrderManagement.Api.Infrastructure.Persistence;
using OrderManagement.Api.Domain.Entities;
using Microsoft.Extensions.Configuration;

namespace OrderManagement.Api.Tests.Infrastructure;

public class PostgresTestFixture : IAsyncLifetime
{
    private readonly string _connectionString;
    public PostgresTestFixture()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.Test.json")
            .AddUserSecrets<PostgresTestFixture>()
            .Build();

        _connectionString = configuration.GetConnectionString(
            "DefaultConnection")
            ?? throw new InvalidOperationException(
                "DefaultConnection is not configured.");
    }

    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString)
            .Options;

        return new AppDbContext(options);
    }

    public async Task InitializeAsync()
    {
        await using var db = CreateDbContext();

        await db.Database.MigrateAsync();

        if (!await db.Products.AnyAsync())
        {
            db.Products.AddRange(
                new Product
                {
                    Name = "Product X",
                    StockQuantity = 15,
                    Price = 100000
                },
                new Product
                {
                    Name = "Product Y",
                    StockQuantity = 50,
                    Price = 75000
                },
                new Product
                {
                    Name = "Product Z",
                    StockQuantity = 100,
                    Price = 50000
                });

            await db.SaveChangesAsync();
        }
    }

    public async Task ResetDatabaseAsync()
    {
        await using var db = CreateDbContext();

        await db.OrderItems.ExecuteDeleteAsync();
        await db.Orders.ExecuteDeleteAsync();

        await db.Products
            .Where(p => p.Id == 1)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.StockQuantity, 15));

        await db.Products
            .Where(p => p.Id == 2)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.StockQuantity, 50));

        await db.Products
            .Where(p => p.Id == 3)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.StockQuantity, 100));
    }

    public async Task DisposeAsync()
    {
        await ResetDatabaseAsync();
    }
}
