using Tickets.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Tickets.Tiers;

public class TiersDbContext(DbContextOptions<TiersDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "tickets";

    public DbSet<Tier> Tiers => Set<Tier>();
    public DbSet<TicketHold> Holds => Set<TicketHold>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

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
    }
}
