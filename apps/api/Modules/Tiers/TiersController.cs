using Tickets.Api.Data;
using Tickets.Api.Modules.Orders;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Tickets.Api.Modules.Tiers;

public record CreateTierRequest(string? Code, string? Name, string? Section, string? Venue, DateTimeOffset? ShowDate, long TicketsTotal, long PriceCents);

public record TierDto(Guid Id, string Code, string Name, string Section, string Venue, DateTimeOffset ShowDate, long PriceCents, int TicketsTotal, int TicketsLeft)
{
    public static TierDto From(Tier s) => new(s.Id, s.Code, s.Name, s.Section, s.Venue, s.ShowDate, s.PriceCents, s.TicketsTotal, s.TicketsLeft);
}

[ApiController]
[Route("api/tiers")]
public class TiersController(TicketsDbContext db) : ControllerBase
{
    private const int MaxTicketsTotal = 5000;
    private const long MaxPriceCents = 1_000_000;

    [HttpGet]
    public async Task<IEnumerable<TierDto>> List(CancellationToken ct) =>
        (await db.Tiers.AsNoTracking().OrderBy(s => s.ShowDate).ThenBy(s => s.Code).ToListAsync(ct)).Select(TierDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<TierDto>> Get(string code, CancellationToken ct)
    {
        var tier = await db.Tiers.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return tier is null ? NotFound() : TierDto.From(tier);
    }

    /// <summary>Opens a tier. This is the only place TicketsLeft is initialised; after that only the Tiers handler writes it.</summary>
    [HttpPost]
    public async Task<ActionResult<TierDto>> Create(CreateTierRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Section) || string.IsNullOrWhiteSpace(request.Venue))
            throw new DomainException(400, "Name, section and venue are required.");
        if (request.TicketsTotal < 1 || request.TicketsTotal > MaxTicketsTotal)
            throw new DomainException(400, $"Tickets on sale must be between 1 and {MaxTicketsTotal} tickets.");
        if (request.PriceCents < 1 || request.PriceCents > MaxPriceCents)
            throw new DomainException(400, $"Price must be between 1 and {MaxPriceCents} cents per ticket.");
        if (await db.Tiers.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Tier '{code}' already exists.");

        var tier = new Tier
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = request.Name.Trim(),
            Section = request.Section.Trim(),
            Venue = request.Venue.Trim(),
            ShowDate = request.ShowDate ?? DateTimeOffset.UtcNow.AddDays(14),
            PriceCents = request.PriceCents,
            TicketsTotal = (int)request.TicketsTotal,
            TicketsLeft = (int)request.TicketsTotal,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Tiers.Add(tier);
        await db.SaveChangesAsync(ct);
        return Created($"/api/tiers/{tier.Code}", TierDto.From(tier));
    }
}
