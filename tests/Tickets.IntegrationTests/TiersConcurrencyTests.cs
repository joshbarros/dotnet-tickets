using System.Collections.Concurrent;
using Tickets.Tiers;
using Tickets.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tickets.IntegrationTests;

public class TiersConcurrencyTests
{
    [Fact]
    public async Task LastTicket_TwoConcurrentOrders_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var tier = await TestDatabase.AddTierAsync(ticketsTotal: 1);

        var outcomes = new ConcurrentBag<(Guid Order, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var orderId = Guid.CreateVersion7();
            await using var db = TestDatabase.Tiers();
            var outcome = await new TiersHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveTickets(orderId, Guid.NewGuid(), tier.Id, 1, $"BUYER-{n}"));
            outcomes.Add((orderId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Tiers();
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
        await TestDatabase.OpenAsync();
        var tier = await TestDatabase.AddTierAsync(ticketsTotal: 4);
        var orderId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Tiers())
            await new TiersHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveTickets(orderId, Guid.NewGuid(), tier.Id, 3, "BUYER-1"));

        // Two different commands (poison compensation and a refund) both ask for the same tickets back.
        await using var first = TestDatabase.Tiers();
        await using var second = TestDatabase.Tiers();
        var one = await new TiersHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleaseTickets(orderId, "poison"));
        var two = await new TiersHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleaseTickets(orderId, "refund"));

        await using var check = TestDatabase.Tiers();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Tiers.Where(s => s.Id == tier.Id).Select(s => s.TicketsLeft).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var tier = await TestDatabase.AddTierAsync(ticketsTotal: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveTickets(Guid.CreateVersion7(), Guid.NewGuid(), tier.Id, 2, "BUYER-1");

        await using var a = TestDatabase.Tiers();
        await using var b = TestDatabase.Tiers();
        var first = await new TiersHandler(a).ReserveAsync(messageId, command);
        var second = await new TiersHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Tiers();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Tiers.Where(s => s.Id == tier.Id).Select(s => s.TicketsLeft).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var tier = await TestDatabase.AddTierAsync(ticketsTotal: 2);
        await using var db = TestDatabase.Tiers();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Tiers.Where(s => s.Id == tier.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TicketsLeft, x => x.TicketsLeft - 3)));
    }
}
