using Tickets.Api.Data;
using Tickets.Api.Messaging;
using Tickets.Api.Modules.Tiers;
using Tickets.Api.Modules.ETickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Tickets.Api.Modules.Orders;

public enum OrderStatus { Requested, Held, Sold, Refunded, Rejected }

public class Order
{
    public Guid Id { get; set; }
    public Guid TierId { get; set; }
    public string TierCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Tickets { get; set; }
    public long TotalCents { get; set; }
    public OrderStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? TicketNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? TicketsReleasedAt { get; set; }
}

public class OrderOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the refund race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public class DomainException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public record CreateOrderRequest(string? TierCode, string? Reference, long Tickets);

public record OrderDto(
    Guid Id, string TierCode, string Reference, int Tickets, long TotalCents, string Status,
    string? Reason, string? TicketNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? TicketsReleasedAt)
{
    public static OrderDto From(Order b) => new(
        b.Id, b.TierCode, b.Reference, b.Tickets, b.TotalCents, b.Status.ToString(),
        b.Reason, b.TicketNumber, b.CreatedAt, b.UpdatedAt, b.TicketsReleasedAt);
}

/// <summary>HTTP side of the saga: writes the order and its first outbox row in one local transaction.</summary>
public class OrderService(TicketsDbContext db)
{
    public async Task<Order> CreateAsync(CreateOrderRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TierCode))
            throw new DomainException(400, "Tier code is required.");
        var error = OrderRules.Validate(request.Reference, request.Tickets);
        if (error is not null)
            throw new DomainException(400, error);

        var tier = await db.Tiers.AsNoTracking().FirstOrDefaultAsync(s => s.Code == request.TierCode.Trim(), ct)
            ?? throw new DomainException(404, $"Tier '{request.TierCode}' does not exist.");

        var tickets = (int)request.Tickets;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            TierId = tier.Id,
            TierCode = tier.Code,
            Reference = reference,
            Tickets = tickets,
            TotalCents = OrderRules.TotalCents(tier.PriceCents, tickets),
            Status = OrderStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Orders.Add(order);
        db.Enqueue("orders", Topology.ReserveKey, order.Id, new ReserveTickets(order.Id, tier.Id, tickets, reference));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainException(409, $"Reference '{reference}' is already booked on {tier.Code}.");
        }
        await tx.CommitAsync(ct);
        return order;
    }

    /// <summary>Moves Held or Sold to Refunded and asks Tiers to give the tickets back. The eticket is not touched.</summary>
    public async Task<Order> RefundAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var moved = await db.Orders
            .Where(b => b.Id == id && (b.Status == OrderStatus.Held || b.Status == OrderStatus.Sold))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Refunded)
                .SetProperty(b => b.Reason, "Refunded by request.")
                .SetProperty(b => b.UpdatedAt, now), ct);
        if (moved != 1)
        {
            var current = await db.Orders.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
                ?? throw new DomainException(404, "Order not found.");
            throw new DomainException(409, $"An order in status {current.Status} cannot be refunded.");
        }

        db.Enqueue("orders", Topology.ReleaseKey, id, new ReleaseTickets(id, "refund"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Orders.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move an order forward. Each step runs only from the status it expects.</summary>
public class OrdersHandler(TicketsDbContext db, IOptions<OrderOptions> options) : IMessageHandler
{
    public const string Name = "orders";

    public Task HandleAsync(Delivery d, CancellationToken ct) => d.RoutingKey switch
    {
        Topology.HeldKey => MarkHeldAsync(d.MessageId, Json.Parse<TicketsHeld>(d.Body).OrderId, ct),
        Topology.ShortKey => MarkShortAsync(d.MessageId, Json.Parse<TicketsShort>(d.Body), ct),
        Topology.IssuedKey => MarkSoldAsync(d.MessageId, Json.Parse<ETicketIssued>(d.Body), ct),
        Topology.RejectedKey => MarkRejectedAsync(d.MessageId, Json.Parse<ETicketRejected>(d.Body), ct),
        Topology.ReleasedKey => MarkReleasedAsync(d.MessageId, Json.Parse<TicketsReleased>(d.Body), ct),
        _ => throw new InvalidOperationException($"Orders does not handle '{d.RoutingKey}'."),
    };

    public Task<MarkOutcome> MarkHeldAsync(Guid messageId, Guid orderId, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == orderId && b.Status == OrderStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Held)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkShortAsync(Guid messageId, TicketsShort m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && b.Status == OrderStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Rejected)
                .SetProperty(b => b.Reason, $"Short: {m.RequestedTickets} tickets wanted, {m.TicketsLeft} left.")
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    /// <summary>The final mark. If a refund got there first the row is no longer Held and nothing changes.</summary>
    public async Task<MarkOutcome> MarkSoldAsync(Guid messageId, ETicketIssued m, CancellationToken ct = default)
    {
        if (options.Value.FinalMarkDelayMs > 0)
            await Task.Delay(options.Value.FinalMarkDelayMs, ct);

        return await StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && b.Status == OrderStatus.Held)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Sold)
                .SetProperty(b => b.TicketNumber, m.Number)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct),
            orderIdForOrderCheck: m.OrderId);
    }

    public Task<MarkOutcome> MarkRejectedAsync(Guid messageId, ETicketRejected m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && (b.Status == OrderStatus.Held || b.Status == OrderStatus.Requested))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Rejected)
                .SetProperty(b => b.Reason, m.Reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkReleasedAsync(Guid messageId, TicketsReleased m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && b.TicketsReleasedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.TicketsReleasedAt, DateTimeOffset.UtcNow), ct));

    private async Task<MarkOutcome> StepAsync(Guid messageId, CancellationToken ct, Func<Task<int>> update, Guid? orderIdForOrderCheck = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return MarkOutcome.Duplicate;
        }

        var changed = await update();
        if (changed == 0 && orderIdForOrderCheck is { } id)
        {
            // Skipped for a reason we can name? Requested means the "Held" mark has not landed yet: retry, do not drop.
            var status = await db.Orders.AsNoTracking().Where(b => b.Id == id).Select(b => b.Status).FirstOrDefaultAsync(ct);
            if (status == OrderStatus.Requested)
                throw new OutOfOrderException("Final mark arrived before the Held mark.");
        }

        await tx.CommitAsync(ct);
        return changed == 1 ? MarkOutcome.Marked : MarkOutcome.Skipped;
    }
}
