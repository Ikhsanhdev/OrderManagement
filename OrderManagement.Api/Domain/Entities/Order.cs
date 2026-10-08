using OrderManagement.Api.Domain.Enums;
namespace OrderManagement.Api.Domain.Entities;

public class Order
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public string ShippingAddress { get; set; } = string.Empty;
    public OrderStatus Status { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long Version { get; set; }
    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
}
