using OrderManagement.Api.DTOs;
namespace OrderManagement.Api.Services;

public class CreateOrderResult
{
    public required OrderResponse Order { get; init; }
    public bool IsExisting { get; init; }
}
