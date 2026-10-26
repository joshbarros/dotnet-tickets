using Tickets.Shared;
using Tickets.Shared.Contracts;
using Tickets.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Tickets.Tiers;

public static class TiersModule
{
    public static IServiceCollection AddTiersModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Tickets") ?? throw new InvalidOperationException("Connection string 'Tickets' is not configured.");
        services.AddDbContext<TiersDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", TiersDbContext.SchemaName)));
        services.AddScoped<TiersHandler>();
        services.AddScoped<ITierCatalog, TierCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<TiersDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<TiersDbContext>>("tickets-db", tags: ["ready"]);

        messaging.AddOutbox<TiersDbContext>()
            .AddConsumer<TiersHandler>(Topology.TiersQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
