using Tickets.Api.Data;
using Tickets.Api.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Tickets.Api.Modules.Tiers;

public enum ReserveOutcome { Held, Short, Duplicate }

/// <summary>
/// The only writer of Tier.TicketsLeft and of ticket holds.
/// The order is one conditional UPDATE: it matches only while the remaining Tickets still covers the request.
/// </summary>
public class TiersHandler(TicketsDbContext db) : IMessageHandler
{
    public const string Name = "tiers";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveTickets>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseTickets>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Tiers does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveTickets command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var updated = await db.Tiers
            .Where(s => s.Id == command.TierId && s.TicketsLeft >= command.Tickets)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TicketsLeft, x => x.TicketsLeft - command.Tickets), ct);

        ReserveOutcome outcome;
        if (updated == 1)
        {
            db.Holds.Add(new TicketHold
            {
                OrderId = command.OrderId,
                TierId = command.TierId,
                Tickets = command.Tickets,
                Status = HoldStatus.Held,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("tiers", Topology.HeldKey, command.OrderId,
                new TicketsHeld(command.OrderId, command.TierId, command.Tickets, command.Reference));
            outcome = ReserveOutcome.Held;
        }
        else
        {
            var remaining = await db.Tiers.Where(s => s.Id == command.TierId)
                .Select(s => s.TicketsLeft).FirstOrDefaultAsync(ct);
            db.Enqueue("tiers", Topology.ShortKey, command.OrderId,
                new TicketsShort(command.OrderId, command.Reference, command.Tickets, remaining));
            outcome = ReserveOutcome.Short;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>Gives held tickets back exactly once. A second release for the same order changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseTickets command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.OrderId == command.OrderId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.OrderId == command.OrderId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Tiers
                    .Where(s => s.Id == hold.TierId && s.TicketsLeft + hold.Tickets <= s.TicketsTotal)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.TicketsLeft, x => x.TicketsLeft + hold.Tickets), ct);
                db.Enqueue("tiers", Topology.ReleasedKey, command.OrderId,
                    new TicketsReleased(command.OrderId, hold.Tickets, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}
