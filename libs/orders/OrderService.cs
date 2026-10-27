using Tickets.Shared;
using Tickets.Shared.Auth;
using Tickets.Shared.Contracts;
using Tickets.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Tickets.Orders;

/// <summary>HTTP side of the saga: writes the order and its first outbox row in one local transaction.</summary>
public class OrderService(OrdersDbContext db, ITierCatalog tiers, ICurrentUser user)
{
    public async Task<Order> CreateAsync(CreateOrderRequest request, CancellationToken ct)
    {
        var ownerId = user.Id ?? throw new DomainException(401, "Sign in to book.");
        if (string.IsNullOrWhiteSpace(request.TierCode))
            throw new DomainException(400, "Tier code is required.");
        var error = OrderRules.Validate(request.Reference, request.Tickets);
        if (error is not null)
            throw new DomainException(400, error);

        var tier = await tiers.FindAsync(request.TierCode.Trim(), ct)
            ?? throw new DomainException(404, $"Tier '{request.TierCode}' does not exist.");

        var tickets = (int)request.Tickets;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
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
        db.Enqueue("orders", Topology.ReserveKey, order.Id, new ReserveTickets(order.Id, ownerId, tier.Id, tickets, reference));
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

    /// <summary>The owner or staff only. Anyone else gets a 404, so ids cannot be probed.</summary>
    public async Task<Order> GetAsync(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (order is null || !OwnerAccess.CanAccess(user, order.OwnerId))
            throw new DomainException(404, "Order not found.");
        return order;
    }

    public async Task<(List<Order> Items, int Total)> ListAsync(string? tierCode, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking();
        if (!user.IsStaff)
        {
            var me = user.Id ?? throw new DomainException(401, "Sign in to see orders.");
            query = query.Where(b => b.OwnerId == me);
        }
        if (!string.IsNullOrWhiteSpace(tierCode))
            query = query.Where(b => b.TierCode == tierCode);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(b => b.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    /// <summary>Moves Held or Sold to Refunded and asks Tiers to give the tickets back. The eticket is not touched.</summary>
    public async Task<Order> RefundAsync(Guid id, CancellationToken ct)
    {
        await GetAsync(id, ct); // 404 unless the caller may see it

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
            var current = await db.Orders.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            throw new DomainException(409, $"An order in status {current.Status} cannot be refunded.");
        }

        db.Enqueue("orders", Topology.ReleaseKey, id, new ReleaseTickets(id, "refund"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Orders.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}
