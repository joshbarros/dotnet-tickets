using Tickets.Api.Data;
using Tickets.Api.Messaging;
using Tickets.Api.Middleware;
using Tickets.Api.Modules.Orders;
using Tickets.Api.Modules.Tiers;
using Tickets.Api.Modules.ETickets;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.PropertyNameCaseInsensitive = true);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("Tickets")
    ?? throw new InvalidOperationException("Connection string 'Tickets' is not configured.");
builder.Services.AddDbContext<TicketsDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.Configure<RabbitOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection("Kafka"));
builder.Services.Configure<OrderOptions>(builder.Configuration.GetSection("Orders"));
builder.Services.AddSingleton<RabbitConnection>();
builder.Services.AddSingleton<KafkaLog>();

builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<TiersHandler>();
builder.Services.AddScoped<ETicketsHandler>();
builder.Services.AddScoped<OrdersHandler>();

builder.Services.AddHostedService<OutboxRelay>();
builder.Services.AddHostedService(sp => new ModuleConsumer<TiersHandler>(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RabbitConnection>(),
    new ConsumerSpec(Topology.TiersQueue, [Topology.ReserveKey, Topology.ReleaseKey], Prefetch: 1),
    sp.GetRequiredService<ILogger<ModuleConsumer<TiersHandler>>>()));
builder.Services.AddHostedService(sp => new ModuleConsumer<ETicketsHandler>(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RabbitConnection>(),
    new ConsumerSpec(Topology.ETicketsQueue, [Topology.HeldKey], Prefetch: 4),
    sp.GetRequiredService<ILogger<ModuleConsumer<ETicketsHandler>>>()));
builder.Services.AddHostedService(sp => new ModuleConsumer<OrdersHandler>(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RabbitConnection>(),
    new ConsumerSpec(Topology.OrdersQueue,
        [Topology.HeldKey, Topology.ShortKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], Prefetch: 4),
    sp.GetRequiredService<ILogger<ModuleConsumer<OrdersHandler>>>()));

var app = builder.Build();

app.UseMiddleware<ExceptionHandler>();
app.MapControllers();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapGet("/api/health", () => Results.Text("Healthy"));

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<TicketsDbContext>().Database.MigrateAsync();
}

app.Run();
