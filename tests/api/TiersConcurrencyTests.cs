using System.Collections.Concurrent;
using Tickets.Api.Messaging;
using Tickets.Api.Modules.Tiers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tickets.Api.Tests;

public class TiersConcurrencyTests
{
    [Fact]
    public async Task LastTicket_TwoConcurrentOrders_OneHeldOneShortNoHoldForTheLoser()
    {
        var db = await TestDatabase.OpenAsync();
        var tier = await TestDatabase.AddTierAsync(db, ticketsTotal: 1);

        var outcomes = new ConcurrentBag<(Guid Order, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var orderId = Guid.CreateVersion7();
            var handler = new TiersHandler(TestDatabase.Create());
            var outcome = await handler.ReserveAsync(
                Guid.CreateVersion7(), new ReserveTickets(orderId, tier.Id, 1, $"BUYER-{n}"));
            outcomes.Add((orderId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        var check = TestDatabase.Create();
        var remaining = await check.Tiers.Where(s => s.Id == tier.Id).Select(s => s.TicketsLeft).FirstAsync();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Order;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(0, remaining);
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.OrderId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task Release_GivesTheTicketsBackExactlyOnce()
    {
        var db = await TestDatabase.OpenAsync();
        var tier = await TestDatabase.AddTierAsync(db, ticketsTotal: 4);
        var orderId = Guid.CreateVersion7();
        var handler = new TiersHandler(TestDatabase.Create());
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveTickets(orderId, tier.Id, 3, "BUYER-1"));

        // Two different commands (poison compensation and a refund) both ask for the same tickets back.
        var first = await new TiersHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseTickets(orderId, "poison"));
        var second = await new TiersHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseTickets(orderId, "refund"));

        var remaining = await TestDatabase.Create().Tiers.Where(s => s.Id == tier.Id).Select(s => s.TicketsLeft).FirstAsync();
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(4, remaining);
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        var db = await TestDatabase.OpenAsync();
        var tier = await TestDatabase.AddTierAsync(db, ticketsTotal: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveTickets(Guid.CreateVersion7(), tier.Id, 2, "BUYER-1");

        var first = await new TiersHandler(TestDatabase.Create()).ReserveAsync(messageId, command);
        var second = await new TiersHandler(TestDatabase.Create()).ReserveAsync(messageId, command);

        var remaining = await TestDatabase.Create().Tiers.Where(s => s.Id == tier.Id).Select(s => s.TicketsLeft).FirstAsync();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, remaining);
    }
}
