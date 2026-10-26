using Tickets.Api.Messaging;
using Tickets.Api.Modules.Orders;
using Tickets.Api.Modules.Tiers;
using Tickets.Api.Modules.ETickets;
using Microsoft.EntityFrameworkCore;

namespace Tickets.Api.Data;

public class TicketsDbContext(DbContextOptions<TicketsDbContext> options) : DbContext(options)
{
    public DbSet<Tier> Tiers => Set<Tier>();
    public DbSet<TicketHold> Holds => Set<TicketHold>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<ETicket> ETickets => Set<ETicket>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tier>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t => t.HasCheckConstraint("CK_Tiers_Remaining", "\"TicketsLeft\" >= 0 AND \"TicketsLeft\" <= \"TicketsTotal\""));
        });

        b.Entity<TicketHold>(e =>
        {
            e.ToTable("TicketHolds");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Tier>().WithMany().HasForeignKey(x => x.TierId);
        });

        b.Entity<Order>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(OrderRules.MaxReferenceLength);
            e.HasIndex(x => new { x.TierId, x.Reference }).IsUnique();
            e.HasOne<Tier>().WithMany().HasForeignKey(x => x.TierId);
        });

        b.Entity<ETicket>(e =>
        {
            e.ToTable("ETickets");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.SentAt).HasFilter("\"SentAt\" IS NULL");
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });
    }
}
