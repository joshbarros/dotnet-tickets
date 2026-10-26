using Tickets.Api.Data;
using Tickets.Api.Modules.Orders;
using Tickets.Api.Modules.Tiers;
using Microsoft.EntityFrameworkCore;

namespace Tickets.Api.Tests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("TICKETS_TEST_DB")
        ?? "Host=localhost;Port=5477;Database=tickets_test;Username=tickets;Password=tickets";

    private static readonly DbContextOptions<TicketsDbContext> Options =
        new DbContextOptionsBuilder<TicketsDbContext>().UseNpgsql(ConnectionString).Options;

    public static TicketsDbContext Create() => new(Options);

    public static async Task<TicketsDbContext> OpenAsync()
    {
        var db = Create();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE \"InboxMessages\", \"OutboxMessages\", \"ETickets\", \"TicketHolds\", \"Orders\", \"Tiers\" RESTART IDENTITY CASCADE;");
        return db;
    }

    public static async Task<Tier> AddTierAsync(TicketsDbContext db, int ticketsTotal, long priceCents = 5_000)
    {
        var tier = new Tier
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Name = "Test Show",
            Section = "Floor",
            Venue = "Arena Norte",
            ShowDate = DateTimeOffset.UtcNow.AddDays(7),
            PriceCents = priceCents,
            TicketsTotal = ticketsTotal,
            TicketsLeft = ticketsTotal,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Tiers.Add(tier);
        await db.SaveChangesAsync();
        return tier;
    }

    public static async Task<Order> AddOrderAsync(TicketsDbContext db, Tier tier, string reference, int tickets, OrderStatus status)
    {
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            TierId = tier.Id,
            TierCode = tier.Code,
            Reference = reference,
            Tickets = tickets,
            TotalCents = OrderRules.TotalCents(tier.PriceCents, tickets),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }
}
