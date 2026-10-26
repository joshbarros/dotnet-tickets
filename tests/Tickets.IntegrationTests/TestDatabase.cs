using Tickets.Orders;
using Tickets.Tiers;
using Tickets.ETickets;
using Tickets.Shared.Auth;
using Tickets.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Tickets.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("TICKETS_TEST_DB")
        ?? "Host=localhost;Port=5477;Database=tickets_test;Username=tickets;Password=tickets";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static OrdersDbContext Orders() => new(Options<OrdersDbContext>(OrdersDbContext.SchemaName));
    public static TiersDbContext Tiers() => new(Options<TiersDbContext>(TiersDbContext.SchemaName));
    public static ETicketsDbContext ETickets() => new(Options<ETicketsDbContext>(ETicketsDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var orders = Orders();
        await using var tickets = Tiers();
        await using var etickets = ETickets();
        await orders.Database.MigrateAsync();
        await tickets.Database.MigrateAsync();
        await etickets.Database.MigrateAsync();
        await orders.Database.ExecuteSqlRawAsync(
            "TRUNCATE orders.\"InboxMessages\", orders.\"OutboxMessages\", orders.\"Orders\", " +
            "tickets.\"InboxMessages\", tickets.\"OutboxMessages\", tickets.\"TicketHolds\", tickets.\"Tiers\", " +
            "etickets.\"InboxMessages\", etickets.\"OutboxMessages\", etickets.\"ETickets\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Tier> AddTierAsync(int ticketsTotal, long priceCents = 5_000)
    {
        await using var db = Tiers();
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

    public static async Task<Order> AddOrderAsync(Tier tier, string reference, int tickets, OrderStatus status, Guid? ownerId = null)
    {
        await using var db = Orders();
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
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

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Tier[] tiers) : ITierCatalog
{
    public Task<TierInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(tiers.Where(s => s.Code == code).Select(s => new TierInfo(s.Id, s.Code, s.PriceCents)).FirstOrDefault());
}
