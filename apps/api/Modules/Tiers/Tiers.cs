namespace Tickets.Api.Modules.Tiers;

public class Tier
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Section { get; set; } = "";
    public string Venue { get; set; } = "";
    public DateTimeOffset ShowDate { get; set; }
    public long PriceCents { get; set; }
    public int TicketsTotal { get; set; }
    public int TicketsLeft { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class TicketHold
{
    public Guid OrderId { get; set; }
    public Guid TierId { get; set; }
    public int Tickets { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}
