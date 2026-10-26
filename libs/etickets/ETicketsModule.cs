using Tickets.Shared;
using Tickets.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Tickets.ETickets;

public static class ETicketsModule
{
    public static IServiceCollection AddETicketsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Tickets") ?? throw new InvalidOperationException("Connection string 'Tickets' is not configured.");
        services.AddDbContext<ETicketsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", ETicketsDbContext.SchemaName)));
        services.AddScoped<ETicketsHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<ETicketsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<ETicketsDbContext>>("etickets-db", tags: ["ready"]);

        messaging.AddOutbox<ETicketsDbContext>()
            .AddConsumer<ETicketsHandler>(Topology.ETicketsQueue, [Topology.HeldKey], prefetch: 4);
        return services;
    }
}
