namespace Tickets.Shared.Contracts;

public record TierInfo(Guid Id, string Code, long PriceCents);

/// <summary>
/// What Orders may ask Tiers, without referencing it. Tiers implements it over its own tables;
/// Orders never reads another module's schema.
/// </summary>
public interface ITierCatalog
{
    Task<TierInfo?> FindAsync(string code, CancellationToken ct);
}
