using Microsoft.Extensions.Logging.Abstractions;
using OrderManagement.Api.DTOs;
using OrderManagement.Api.Services;
using OrderManagement.Api.Tests.Infrastructure;

namespace OrderManagement.Api.Tests.Services;

public class OrderServiceTests : IClassFixture<PostgresTestFixture>
{
    private readonly PostgresTestFixture _fixture;

    public OrderServiceTests(PostgresTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateOrder_ShouldCreateOrderAndDeductStock()
    {
        await _fixture.ResetDatabaseAsync();

        await using var db = _fixture.CreateDbContext();

        var stockService = new StockService(db);

        var logger = NullLogger<OrderService>.Instance;

        var orderService = new OrderService(
            db,
            logger,
            stockService);

        var request = new CreateOrderRequest
        {
            CustomerId = "test-customer",
            ShippingAddress = "Test Address",
            Items =
            [
                new CreateOrderItemRequest
                {
                    ProductId = 1,
                    Quantity = 2
                }
            ]
        };

        var result = await orderService.CreateAsync(
            request,
            $"test-create-{Guid.NewGuid()}");

        Assert.False(result.IsExisting);
        Assert.NotEqual(Guid.Empty, result.Order.Id);
        Assert.Equal("test-customer", result.Order.CustomerId);
        Assert.Single(result.Order.Items);
        Assert.Equal(2, result.Order.Items[0].Quantity);
    }

    [Fact]
    public async Task ConcurrentOrders_ShouldAllowOnlyOneOrder_WhenStockIsInsufficient()
    {
        await _fixture.ResetDatabaseAsync();

        var task1 = CreateOrderAsync(
            "customer-1",
            "concurrency-key-1");

        var task2 = CreateOrderAsync(
            "customer-2",
            "concurrency-key-2");

        var results = await Task.WhenAll(
            task1,
            task2);

        var successCount = results.Count(x => x.Success);
        var failureCount = results.Count(x => !x.Success);

        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);

        await using var db = _fixture.CreateDbContext();

        var product = await db.Products
            .FindAsync(1);

        Assert.NotNull(product);
        Assert.Equal(5, product.StockQuantity);

        var orderCount = db.Orders.Count();

        Assert.Equal(1, orderCount);
    }

    [Fact]
    public async Task ConcurrentStatusUpdates_ShouldAllowOnlyOneUpdate()
    {
        await _fixture.ResetDatabaseAsync();

        var createTask = CreateOrderAsync(
            "status-customer",
            "status-create-key");

        var createResult = await createTask;

        Assert.True(createResult.Success);
        Assert.NotNull(createResult.OrderId);

        var orderId = createResult.OrderId.Value;

        var task1 = UpdateStatusAsync(orderId);
        var task2 = UpdateStatusAsync(orderId);

        var results = await Task.WhenAll(task1, task2);

        var successCount = results.Count(x => x);
        var conflictCount = results.Count(x => !x);

        Assert.Equal(1, successCount);
        Assert.Equal(1, conflictCount);

        await using var db = _fixture.CreateDbContext();

        var order = await db.Orders
            .FindAsync(orderId);

        Assert.NotNull(order);
        Assert.Equal(
            OrderManagement.Api.Domain.Enums.OrderStatus.Confirmed,
            order.Status);
        Assert.Equal(2, order.Version);
    }

    [Fact]
    public async Task ConcurrentCreateOrders_ShouldCreateOnlyOneOrder_WithSameIdempotencyKey()
    {
        await _fixture.ResetDatabaseAsync();

        var task1 = CreateOrderAsync(
            "customer-c",
            "same-idempotency-key");

        var task2 = CreateOrderAsync(
            "customer-c",
            "same-idempotency-key");

        var results = await Task.WhenAll(task1, task2);

        var successCount = results.Count(x => x.Success);

        Assert.Equal(2, successCount);

        Assert.NotNull(results[0].OrderId);
        Assert.NotNull(results[1].OrderId);

        Assert.Equal(
            results[0].OrderId,
            results[1].OrderId);

        await using var db = _fixture.CreateDbContext();

        var product = await db.Products
            .FindAsync(1);

        Assert.NotNull(product);
        Assert.Equal(5, product.StockQuantity);

        var orderCount = db.Orders.Count();

        Assert.Equal(1, orderCount);
    }

    [Fact]
    public async Task ConcurrentCancel_ShouldAllowOnlyOneCancellation()
    {
        await _fixture.ResetDatabaseAsync();

        var createResult = await CreateOrderAsync(
            "cancel-customer",
            "cancel-create-key");

        Assert.True(createResult.Success);
        Assert.NotNull(createResult.OrderId);

        var orderId = createResult.OrderId.Value;

        var task1 = CancelOrderAsync(orderId);
        var task2 = CancelOrderAsync(orderId);

        var results = await Task.WhenAll(task1, task2);

        var successCount = results.Count(x => x);
        var conflictCount = results.Count(x => !x);

        Assert.Equal(1, successCount);
        Assert.Equal(1, conflictCount);

        await using var db = _fixture.CreateDbContext();

        var order = await db.Orders
            .FindAsync(orderId);

        Assert.NotNull(order);

        Assert.Equal(
            OrderManagement.Api.Domain.Enums.OrderStatus.Cancelled,
            order.Status);

        Assert.Equal(2, order.Version);

        var product = await db.Products
            .FindAsync(1);

        Assert.NotNull(product);

        Assert.Equal(15, product.StockQuantity);
    }

    [Fact]
    public async Task CreateOrder_ShouldRejectZeroQuantity()
    {
        await _fixture.ResetDatabaseAsync();

        await using var db = _fixture.CreateDbContext();

        var stockService = new StockService(db);

        var orderService = new OrderService(
            db,
            NullLogger<OrderService>.Instance,
            stockService);

        var request = new CreateOrderRequest
        {
            CustomerId = "validation-customer",
            ShippingAddress = "Validation Address",
            Items =
            [
                new CreateOrderItemRequest
                {
                    ProductId = 1,
                    Quantity = 0
                }
            ]
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            orderService.CreateAsync(
                request,
                $"validation-zero-{Guid.NewGuid()}"));
    }

    [Fact]
    public async Task CreateOrder_ShouldRejectNegativeQuantity()
    {
        await _fixture.ResetDatabaseAsync();

        await using var db = _fixture.CreateDbContext();

        var stockService = new StockService(db);

        var orderService = new OrderService(
            db,
            NullLogger<OrderService>.Instance,
            stockService);

        var request = new CreateOrderRequest
        {
            CustomerId = "validation-customer",
            ShippingAddress = "Validation Address",
            Items =
            [
                new CreateOrderItemRequest
                {
                    ProductId = 1,
                    Quantity = -1
                }
            ]
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            orderService.CreateAsync(
                request,
                $"validation-negative-{Guid.NewGuid()}"));
    }

    [Fact]
    public async Task CreateOrder_ShouldRejectEmptyItems()
    {
        await _fixture.ResetDatabaseAsync();

        await using var db = _fixture.CreateDbContext();

        var stockService = new StockService(db);

        var orderService = new OrderService(
            db,
            NullLogger<OrderService>.Instance,
            stockService);

        var request = new CreateOrderRequest
        {
            CustomerId = "validation-customer",
            ShippingAddress = "Validation Address",
            Items = []
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            orderService.CreateAsync(
                request,
                $"validation-empty-items-{Guid.NewGuid()}"));
    }

    [Fact]
    public async Task CreateOrder_ShouldRejectEmptyCustomerId()
    {
        await _fixture.ResetDatabaseAsync();

        await using var db = _fixture.CreateDbContext();

        var stockService = new StockService(db);

        var orderService = new OrderService(
            db,
            NullLogger<OrderService>.Instance,
            stockService);

        var request = new CreateOrderRequest
        {
            CustomerId = "",
            ShippingAddress = "Validation Address",
            Items =
            [
                new CreateOrderItemRequest
                {
                    ProductId = 1,
                    Quantity = 1
                }
            ]
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            orderService.CreateAsync(
                request,
                $"validation-customer-{Guid.NewGuid()}"));
    }

    [Fact]
    public async Task CreateOrder_ShouldRejectEmptyShippingAddress()
    {
        await _fixture.ResetDatabaseAsync();

        await using var db = _fixture.CreateDbContext();

        var stockService = new StockService(db);

        var orderService = new OrderService(
            db,
            NullLogger<OrderService>.Instance,
            stockService);

        var request = new CreateOrderRequest
        {
            CustomerId = "validation-customer",
            ShippingAddress = "",
            Items =
            [
                new CreateOrderItemRequest
                {
                    ProductId = 1,
                    Quantity = 1
                }
            ]
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            orderService.CreateAsync(
                request,
                $"validation-address-{Guid.NewGuid()}"));
    }

    [Fact]
    public async Task GetList_ShouldRejectZeroPage()
    {
        await _fixture.ResetDatabaseAsync();

        await using var db = _fixture.CreateDbContext();

        var stockService = new StockService(db);

        var orderService = new OrderService(
            db,
            NullLogger<OrderService>.Instance,
            stockService);

        var query = new OrderListQuery
        {
            Page = 0,
            PageSize = 10
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            orderService.GetListAsync(query));
    }

    [Fact]
    public async Task GetList_ShouldRejectZeroPageSize()
    {
        await _fixture.ResetDatabaseAsync();

        await using var db = _fixture.CreateDbContext();

        var stockService = new StockService(db);

        var orderService = new OrderService(
            db,
            NullLogger<OrderService>.Instance,
            stockService);

        var query = new OrderListQuery
        {
            Page = 1,
            PageSize = 0
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            orderService.GetListAsync(query));
    }

    [Fact]
    public async Task UpdateStatus_ShouldRejectInvalidStatus()
    {
        await _fixture.ResetDatabaseAsync();

        var createResult = await CreateOrderAsync(
            "invalid-status-customer",
            "invalid-status-create-key");

        Assert.True(createResult.Success);
        Assert.NotNull(createResult.OrderId);

        await using var db = _fixture.CreateDbContext();

        var stockService = new StockService(db);

        var orderService = new OrderService(
            db,
            NullLogger<OrderService>.Instance,
            stockService);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            orderService.UpdateStatusAsync(
                createResult.OrderId.Value,
                "InvalidStatus"));
    }

    [Fact]
    public async Task GetList_ShouldRejectPageSizeGreaterThan100()
    {
        await _fixture.ResetDatabaseAsync();

        await using var db = _fixture.CreateDbContext();

        var stockService = new StockService(db);

        var orderService = new OrderService(
            db,
            NullLogger<OrderService>.Instance,
            stockService);

        var query = new OrderListQuery
        {
            Page = 1,
            PageSize = 101
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            orderService.GetListAsync(query));
    }

    private async Task<(bool Success, Guid? OrderId)> CreateOrderAsync(string customerId, string idempotencyKey)
    {
        await using var db = _fixture.CreateDbContext();

        var stockService = new StockService(db);

        var orderService = new OrderService(
            db,
            NullLogger<OrderService>.Instance,
            stockService);

        var request = new CreateOrderRequest
        {
            CustomerId = customerId,
            ShippingAddress = "Concurrency Test Address",
            Items =
            [
                new CreateOrderItemRequest
                {
                    ProductId = 1,
                    Quantity = 10
                }
            ]
        };

        try
        {
            var result = await orderService.CreateAsync(
                request,
                idempotencyKey);

            return (true, result.Order.Id);
        }
        catch (InvalidOperationException)
        {
            return (false, null);
        }
    }

    private async Task<bool> UpdateStatusAsync(Guid orderId)
    {
        await using var db = _fixture.CreateDbContext();

        var stockService = new StockService(db);

        var orderService = new OrderService(
            db,
            NullLogger<OrderService>.Instance,
            stockService);

        try
        {
            await orderService.UpdateStatusAsync(
                orderId,
                "Confirmed");

            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<bool> CancelOrderAsync(Guid orderId)
    {
        await using var db = _fixture.CreateDbContext();

        var stockService = new StockService(db);

        var orderService = new OrderService(
            db,
            NullLogger<OrderService>.Instance,
            stockService);

        try
        {
            await orderService.CancelAsync(orderId);

            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
