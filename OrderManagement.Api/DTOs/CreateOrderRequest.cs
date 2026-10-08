namespace OrderManagement.Api.DTOs;

public class CreateOrderRequest
{
    public string CustomerId { get; set; } = string.Empty;
    public List<CreateOrderItemRequest> Items { get; set; } = [];
    public string ShippingAddress { get; set; } = string.Empty;
}
