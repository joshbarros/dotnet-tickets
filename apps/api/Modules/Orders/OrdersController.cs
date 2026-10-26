using System.Text.Json;
using Tickets.Api.Data;
using Tickets.Api.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Tickets.Api.Modules.Orders;

[ApiController]
[Route("api/orders")]
public class OrdersController(TicketsDbContext db, OrderService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the order. The ticket hold and the eticket follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<OrderDto>> Create(CreateOrderRequest request, CancellationToken ct)
    {
        var order = await service.CreateAsync(request, ct);
        return Accepted($"/api/orders/{order.Id}", OrderDto.From(order));
    }

    [HttpGet]
    public async Task<IEnumerable<OrderDto>> List([FromQuery] string? tierCode, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(tierCode))
            query = query.Where(b => b.TierCode == tierCode);
        return (await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct)).Select(OrderDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderDto>> Get(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        return order is null ? NotFound() : OrderDto.From(order);
    }

    [HttpPost("{id:guid}/refund")]
    public async Task<ActionResult<OrderDto>> Refund(Guid id, CancellationToken ct) =>
        Accepted($"/api/orders/{id}", OrderDto.From(await service.RefundAsync(id, ct)));

    /// <summary>The Kafka event log for one order, read from the start of the topic.</summary>
    [HttpGet("{id:guid}/events")]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
