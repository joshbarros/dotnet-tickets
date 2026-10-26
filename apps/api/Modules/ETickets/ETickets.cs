using Tickets.Api.Data;
using Tickets.Api.Messaging;
using Tickets.Api.Modules.Orders;
using Microsoft.EntityFrameworkCore;

namespace Tickets.Api.Modules.ETickets;

public enum ETicketStatus { Issued, Rejected }

public class ETicket
{
    public Guid OrderId { get; set; }
    public string? Number { get; set; }
    public ETicketStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the eticket once the tickets are held. A poison reference is committed as rejected.</summary>
public class ETicketsHandler(TicketsDbContext db) : IMessageHandler
{
    public const string Name = "etickets";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"ETickets does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<TicketsHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, TicketsHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (OrderRules.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.ETickets.Add(new ETicket
            {
                OrderId = held.OrderId,
                Status = ETicketStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("etickets", Topology.ReleaseKey, held.OrderId, new ReleaseTickets(held.OrderId, "poison"));
            db.Enqueue("etickets", Topology.RejectedKey, held.OrderId, new ETicketRejected(held.OrderId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"ET-{held.OrderId.ToString("N")[^12..].ToUpperInvariant()}";
            db.ETickets.Add(new ETicket
            {
                OrderId = held.OrderId,
                Number = number,
                Status = ETicketStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("etickets", Topology.IssuedKey, held.OrderId, new ETicketIssued(held.OrderId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
