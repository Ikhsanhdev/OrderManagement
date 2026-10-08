using OrderManagement.Api.Domain.Enums;

namespace OrderManagement.Api.DTOs;

public class OrderListQuery
{
    public OrderStatus? Status { get; set; }
    public string? CustomerId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
}
