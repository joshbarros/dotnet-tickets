using System.Text.Json;

namespace Tickets.Shared.Messaging;

public record ReserveTickets(Guid OrderId, Guid OwnerId, Guid TierId, int Tickets, string Reference);
public record TicketsHeld(Guid OrderId, Guid OwnerId, Guid TierId, int Tickets, string Reference);
public record TicketsShort(Guid OrderId, string Reference, int RequestedTickets, int TicketsLeft);
public record ReleaseTickets(Guid OrderId, string Reason);
public record TicketsReleased(Guid OrderId, int Tickets, string Reason);
public record ETicketIssued(Guid OrderId, string Number);
public record ETicketRejected(Guid OrderId, string Reason);

public record Delivery(Guid MessageId, string RoutingKey, string Body);

public interface IMessageHandler
{
    Task HandleAsync(Delivery delivery, CancellationToken ct);
}

/// <summary>Committed as rejected; the message must be dead-lettered, not retried.</summary>
public class PoisonMessageException(string message) : Exception(message);

/// <summary>The message arrived before the state it depends on; retry later.</summary>
public class OutOfOrderException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty message body.");
}
