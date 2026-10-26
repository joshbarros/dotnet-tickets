namespace Tickets.Orders;

public enum OrderStatus { Requested, Held, Sold, Refunded, Rejected }

public class Order
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid TierId { get; set; }
    public string TierCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Tickets { get; set; }
    public long TotalCents { get; set; }
    public OrderStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? TicketNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? TicketsReleasedAt { get; set; }
}

public class OrderOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the refund race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreateOrderRequest(string? TierCode, string? Reference, long Tickets);

public record OrderDto(
    Guid Id, Guid OwnerId, string TierCode, string Reference, int Tickets, long TotalCents, string Status,
    string? Reason, string? TicketNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? TicketsReleasedAt)
{
    public static OrderDto From(Order b) => new(
        b.Id, b.OwnerId, b.TierCode, b.Reference, b.Tickets, b.TotalCents, b.Status.ToString(),
        b.Reason, b.TicketNumber, b.CreatedAt, b.UpdatedAt, b.TicketsReleasedAt);
}
