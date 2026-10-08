using Microsoft.AspNetCore.Mvc;
using OrderManagement.Api.DTOs;
using OrderManagement.Api.Services;
namespace OrderManagement.Api.Controllers;

[ApiController]
[Route("api/orders")]
public class OrdersController : ControllerBase
{
    private readonly OrderService _orderService;
    public OrdersController(OrderService orderService)
    {
        _orderService = orderService;
    }

    [HttpPost]
    public async Task<ActionResult<OrderResponse>> Create([FromBody] CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return BadRequest(new
            {
                message = "Idempotency-Key header is required."
            });
        }

        var result = await _orderService.CreateAsync(
            request,
            idempotencyKey,
            cancellationToken);

        if(result.IsExisting) {
            return Ok(result.Order);
        }

        return CreatedAtAction(
            nameof(GetById),
            new { id = result.Order.Id },
            result.Order);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var order = await _orderService.GetByIdAsync(id, cancellationToken);
        return Ok(order);
    }

    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult<OrderResponse>> UpdateStatus(Guid id, [FromBody] UpdateOrderStatusRequest request, CancellationToken cancellationToken)
    {
        var order = await _orderService.UpdateStatusAsync(id, request.Status, cancellationToken);
        return Ok(order);
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<OrderResponse>> Cancel(Guid id, CancellationToken cancellationToken)
    {
        var order = await _orderService.CancelAsync(id, cancellationToken);
        return Ok(order);
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<OrderResponse>>> GetList([FromQuery] OrderListQuery query, CancellationToken cancellationToken)
    {
        var result = await _orderService.GetListAsync(query, cancellationToken);
        return Ok(result);
    }
}
