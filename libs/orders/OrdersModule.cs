using Tickets.Shared;
using Tickets.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Tickets.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Tickets") ?? throw new InvalidOperationException("Connection string 'Tickets' is not configured.");
        services.AddDbContext<OrdersDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", OrdersDbContext.SchemaName)));
        services.Configure<OrderOptions>(config.GetSection("Orders"));
        services.AddScoped<OrderService>();
        services.AddScoped<OrdersHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<OrdersDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<OrdersDbContext>>("orders-db", tags: ["ready"]);

        messaging.AddOutbox<OrdersDbContext>()
            .AddConsumer<OrdersHandler>(Topology.OrdersQueue,
                [Topology.HeldKey, Topology.ShortKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], prefetch: 4);
        return services;
    }
}
