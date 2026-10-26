using Tickets.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Tickets.Api.Modules.ETickets;

public record ETicketDto(Guid OrderId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/e-tickets")]
public class ETicketsController(TicketsDbContext db) : ControllerBase
{
    [HttpGet("{orderId:guid}")]
    public async Task<ActionResult<ETicketDto>> Get(Guid orderId, CancellationToken ct)
    {
        var doc = await db.ETickets.AsNoTracking().FirstOrDefaultAsync(d => d.OrderId == orderId, ct);
        return doc is null
            ? NotFound()
            : new ETicketDto(doc.OrderId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
