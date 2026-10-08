using Microsoft.EntityFrameworkCore;
using OrderManagement.Api.Domain.Entities;
using OrderManagement.Api.Domain.Enums;
using OrderManagement.Api.DTOs;
using OrderManagement.Api.Infrastructure.Persistence;

namespace OrderManagement.Api.Services;

public class OrderService
{
    private const string IdempotencyKeyConstraint = "IX_Orders_IdempotencyKey";
    private readonly AppDbContext _db;
    private readonly ILogger<OrderService> _logger;
    private readonly StockService _stockService;

    public OrderService(AppDbContext db, ILogger<OrderService> logger, StockService stockService)
    {
        _db = db;
        _logger = logger;
        _stockService = stockService;
    }

    public async Task<CreateOrderResult> CreateAsync(CreateOrderRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerId))
        {
            throw new ArgumentException("CustomerId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ShippingAddress))
        {
            throw new ArgumentException("ShippingAddress is required.");
        }

        if (request.Items is null || request.Items.Count == 0)
        {
            throw new ArgumentException("At least one order item is required.");
        }

        if (request.Items.Any(x => x.Quantity <= 0))
        {
            throw new ArgumentException("Item quantity must be greater than zero.");
        }

        // Fast path:
        // If the idempotency key was already committed previously,
        // return the existing order immediately.
        var existingOrder = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(
                o => o.IdempotencyKey == idempotencyKey,
                cancellationToken);

        if (existingOrder is not null)
        {
            _logger.LogInformation("Idempotent request detected. Returning existing order {OrderId}.", existingOrder.Id);
            return new CreateOrderResult
            {
                Order = MapToResponse(existingOrder),
                IsExisting = true
            };
        }

        // Reject duplicate products in one request.
        var duplicateProduct = request.Items
            .GroupBy(x => x.ProductId)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicateProduct is not null)
        {
            throw new InvalidOperationException(
                $"Product {duplicateProduct.Key} appears more than once.");
        }

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            // Load products before creating the order.
            var productIds = request.Items
                .Select(x => x.ProductId)
                .ToList();

            var products = await _db.Products
                .AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, cancellationToken);

            // Validate all products exist.
            foreach (var item in request.Items)
            {
                if (!products.ContainsKey(item.ProductId))
                {
                    throw new KeyNotFoundException(
                        $"Product {item.ProductId} was not found.");
                }
            }

            // Create the order FIRST.
            //
            // The unique index on IdempotencyKey makes this
            // operation race-safe when two requests use the
            // same key concurrently.
            var now = DateTime.UtcNow;

            var order = new Order
            {
                Id = Guid.NewGuid(),
                CustomerId = request.CustomerId,
                ShippingAddress = request.ShippingAddress,
                Status = OrderStatus.Pending,
                IdempotencyKey = idempotencyKey,
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1
            };

            _db.Orders.Add(order);
            await _db.SaveChangesAsync(cancellationToken);

            // Only after successfully claiming the idempotency key,
            // deduct stock atomically.
            foreach (var item in request.Items)
            {
                var deducted = await _stockService.DeductStockAsync(
                    item.ProductId,
                    item.Quantity,
                    cancellationToken);

                if (!deducted)
                {
                    _logger.LogWarning(
                        "Insufficient stock for product {ProductId} when creating order {OrderId}.",
                        item.ProductId,
                        order.Id);

                    throw new InvalidOperationException(
                        $"Insufficient stock for product {item.ProductId}.");
                }
            }

            // Create order items after stock has been reserved.
            foreach (var item in request.Items)
            {
                var product = products[item.ProductId];

                order.Items.Add(new OrderItem
                {
                    Id = Guid.NewGuid(),
                    ProductId = item.ProductId,
                    Quantity = item.Quantity,
                    UnitPrice = product.Price
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation("Order {OrderId} created successfully for customer {CustomerId}.", order.Id, order.CustomerId);

            var createdOrder = await _db.Orders
                .AsNoTracking()
                .Include(o => o.Items)
                .ThenInclude(i => i.Product)
                .FirstAsync(
                    o => o.Id == order.Id,
                    cancellationToken);

            return new CreateOrderResult
            {
                Order = MapToResponse(createdOrder),
                IsExisting = false
            };
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation(IdempotencyKeyConstraint))
        {
            _db.ChangeTracker.Clear();

            var orderAfterRace = await _db.Orders
                .AsNoTracking()
                .Include(o => o.Items)
                .ThenInclude(i => i.Product)
                .FirstAsync(
                    o => o.IdempotencyKey == idempotencyKey,
                    cancellationToken);

            return new CreateOrderResult
            {
                Order = MapToResponse(orderAfterRace),
                IsExisting = true
            };
        }
    }

    public async Task<OrderResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var order = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order is null)
        {
            throw new KeyNotFoundException($"Order {id} was not found.");
        }

        return MapToResponse(order);
    }

    public async Task<PagedResult<OrderResponse>> GetListAsync(OrderListQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Page < 1)
        {
            throw new ArgumentException("Page must be greater than or equal to 1.");
        }

        if (query.PageSize < 1)
        {
            throw new ArgumentException("PageSize must be greater than or equal to 1.");
        }

        if (query.PageSize > 100)
        {
            throw new ArgumentException("PageSize cannot be greater than 100.");
        }

        var ordersQuery = _db.Orders
            .AsNoTracking()
            .AsQueryable();

        if (query.Status.HasValue)
        {
            ordersQuery = ordersQuery.Where(o => o.Status == query.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.CustomerId))
        {
            ordersQuery = ordersQuery.Where(o => o.CustomerId == query.CustomerId);
        }

        if (query.From.HasValue)
        {
            var from = DateTime.SpecifyKind(
                query.From.Value,
                DateTimeKind.Utc);

            ordersQuery = ordersQuery.Where(o => o.CreatedAt >= from);
        }

        if (query.To.HasValue)
        {
            var to = DateTime.SpecifyKind(
                query.To.Value.Date.AddDays(1),
                DateTimeKind.Utc);

            ordersQuery = ordersQuery.Where(
                o => o.CreatedAt < to);
        }

        var totalCount = await ordersQuery.CountAsync(cancellationToken);

        var orders = await ordersQuery
            .OrderByDescending(o => o.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Include(o => o.Items)
            .ThenInclude(i => i.Product)
            .ToListAsync(cancellationToken);

        return new PagedResult<OrderResponse>
        {
            Items = orders.Select(MapToResponse).ToList(),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)query.PageSize)
        };
    }

    public async Task<OrderResponse> UpdateStatusAsync(Guid id, string requestedStatus, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<OrderStatus>(requestedStatus, ignoreCase: true, out var newStatus))
        {
            // throw new InvalidOperationException($"Invalid order status: {requestedStatus}.");
            throw new ArgumentException($"Invalid order status: {requestedStatus}.");
        }

        var order = await _db.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(
                o => o.Id == id,
                cancellationToken);

        if (order is null)
        {
            throw new KeyNotFoundException($"Order {id} was not found.");
        }

        var isValidTransition = order.Status switch
        {
            OrderStatus.Pending =>
                newStatus is OrderStatus.Confirmed
                    or OrderStatus.Cancelled,

            OrderStatus.Confirmed =>
                newStatus is OrderStatus.Shipped
                    or OrderStatus.Cancelled,

            OrderStatus.Shipped =>
                newStatus == OrderStatus.Delivered,

            OrderStatus.Delivered => false,

            OrderStatus.Cancelled => false,

            _ => false
        };

        if (!isValidTransition)
        {
            throw new InvalidOperationException($"Order cannot transition from {order.Status} to {newStatus}.");
        }

        var now = DateTime.UtcNow;

        var affectedRows = await _db.Orders
            .Where(o =>
                o.Id == id &&
                o.Version == order.Version &&
                o.Status == order.Status)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(o => o.Status, newStatus)
                    .SetProperty(o => o.Version, o => o.Version + 1)
                    .SetProperty(o => o.UpdatedAt, now),
                cancellationToken);

        if (affectedRows == 0)
        {
            _logger.LogWarning(
                "Concurrency conflict while updating order {OrderId} from status {OldStatus} to {NewStatus}.",
                id,
                order.Status,
                newStatus);

            throw new InvalidOperationException($"Order {id} was modified by another request.");
        }

        _logger.LogInformation("Order {OrderId} status changed from {OldStatus} to {NewStatus}.", id, order.Status, newStatus);

        var updatedOrder = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .ThenInclude(i => i.Product)
            .FirstAsync(
                o => o.Id == id,
                cancellationToken);

        return MapToResponse(updatedOrder);
    }

    public async Task<OrderResponse> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var order = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(
                o => o.Id == id,
                cancellationToken);

        if (order is null)
        {
            throw new KeyNotFoundException($"Order {id} was not found.");
        }

        if (order.Status is not OrderStatus.Pending and not OrderStatus.Confirmed)
        {
            throw new InvalidOperationException($"Order cannot be cancelled from status {order.Status}.");
        }

        var now = DateTime.UtcNow;
        var affectedRows = await _db.Orders
            .Where(o =>
                o.Id == id &&
                o.Status == order.Status &&
                o.Version == order.Version)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        o => o.Status,
                        OrderStatus.Cancelled)
                    .SetProperty(
                        o => o.Version,
                        o => o.Version + 1)
                    .SetProperty(
                        o => o.UpdatedAt,
                        now),
                cancellationToken);

        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"Order {id} was modified by another request.");
        }

        foreach (var item in order.Items)
        {
            await _stockService.RestoreStockAsync(
                item.ProductId,
                item.Quantity,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("Order {OrderId} cancelled successfully.", id);

        var cancelledOrder = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .ThenInclude(i => i.Product)
            .FirstAsync(
                o => o.Id == id,
                cancellationToken);

        return MapToResponse(cancelledOrder);
    }

    private static OrderResponse MapToResponse(Order order)
    {
        return new OrderResponse
        {
            Id = order.Id,
            CustomerId = order.CustomerId,
            ShippingAddress = order.ShippingAddress,
            Status = order.Status,
            CreatedAt = order.CreatedAt,
            Items = order.Items.Select(item => new OrderItemResponse
            {
                ProductId = item.ProductId,
                ProductName = item.Product.Name,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice
            }).ToList()
        };
    }
}
