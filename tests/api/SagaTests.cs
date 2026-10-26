using Tickets.Api.Messaging;
using Tickets.Api.Modules.Orders;
using Tickets.Api.Modules.Tiers;
using Tickets.Api.Modules.ETickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Tickets.Api.Tests;

public class SagaTests
{
    private static OrdersHandler Orders() =>
        new(TestDatabase.Create(), Options.Create(new OrderOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        var db = await TestDatabase.OpenAsync();
        var tier = await TestDatabase.AddTierAsync(db, 5);
        var order = await TestDatabase.AddOrderAsync(db, tier, "BUYER-POISON-1", 2, OrderStatus.Held);
        var held = new TicketsHeld(order.Id, tier.Id, 2, order.Reference);

        var handler = new ETicketsHandler(TestDatabase.Create());
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            handler.HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        var check = TestDatabase.Create();
        var doc = await check.ETickets.SingleAsync(d => d.OrderId == order.Id);
        Assert.Equal(ETicketStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Refund_ThenFinalMark_LeavesOrderRefundedAndETicketIssued()
    {
        var db = await TestDatabase.OpenAsync();
        var tier = await TestDatabase.AddTierAsync(db, 5);
        var order = await TestDatabase.AddOrderAsync(db, tier, "BUYER-1", 2, OrderStatus.Held);

        // The eticket is issued; the final mark has not landed yet.
        var number = "ET-TEST";
        await new ETicketsHandler(TestDatabase.Create()).IssueAsync(
            Guid.CreateVersion7(), new TicketsHeld(order.Id, tier.Id, 2, order.Reference));

        // The refund gets there first.
        await new OrderService(TestDatabase.Create()).RefundAsync(order.Id, default);

        var mark = await Orders().MarkSoldAsync(Guid.CreateVersion7(), new ETicketIssued(order.Id, number));

        var check = TestDatabase.Create();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(OrderStatus.Refunded, (await check.Orders.SingleAsync()).Status);
        Assert.Equal(ETicketStatus.Issued, (await check.ETickets.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        var db = await TestDatabase.OpenAsync();
        var tier = await TestDatabase.AddTierAsync(db, 5);
        var held = await TestDatabase.AddOrderAsync(db, tier, "BUYER-A", 1, OrderStatus.Held);
        var rejected = await TestDatabase.AddOrderAsync(db, tier, "BUYER-B", 1, OrderStatus.Rejected);
        var requested = await TestDatabase.AddOrderAsync(db, tier, "BUYER-C", 1, OrderStatus.Requested);

        Assert.Equal(MarkOutcome.Marked, await Orders().MarkSoldAsync(Guid.CreateVersion7(), new ETicketIssued(held.Id, "ET-1")));
        Assert.Equal(MarkOutcome.Skipped, await Orders().MarkSoldAsync(Guid.CreateVersion7(), new ETicketIssued(rejected.Id, "ET-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await Assert.ThrowsAsync<OutOfOrderException>(() =>
            Orders().MarkSoldAsync(Guid.CreateVersion7(), new ETicketIssued(requested.Id, "ET-3")));

        var statuses = (await TestDatabase.Create().Orders.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(OrderStatus.Sold, statuses["BUYER-A"]);
        Assert.Equal(OrderStatus.Rejected, statuses["BUYER-B"]);
        Assert.Equal(OrderStatus.Requested, statuses["BUYER-C"]);
    }

    [Fact]
    public async Task Refund_OfARejectedOrder_Conflicts()
    {
        var db = await TestDatabase.OpenAsync();
        var tier = await TestDatabase.AddTierAsync(db, 5);
        var order = await TestDatabase.AddOrderAsync(db, tier, "BUYER-1", 1, OrderStatus.Rejected);

        var ex = await Assert.ThrowsAsync<DomainException>(() => new OrderService(TestDatabase.Create()).RefundAsync(order.Id, default));
        Assert.Equal(409, ex.Status);
    }
}
