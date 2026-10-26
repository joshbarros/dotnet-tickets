namespace Tickets.Api.Modules.Orders;

public static class OrderRules
{
    public const int MaxTicketsPerOrder = 6;
    public const int MaxReferenceLength = 40;
    public const string PoisonToken = "POISON";
    public const long ServiceFeeBasisPoints = 1_200;

    /// <summary>Returns an error message, or null when the request is acceptable.</summary>
    public static string? Validate(string? reference, long tickets)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "Buyer reference is required.";
        if (reference.Trim().Length > MaxReferenceLength)
            return $"Buyer reference must be at most {MaxReferenceLength} characters.";
        if (tickets < 1)
            return "An order needs at least 1 ticket.";
        // Compared as long on purpose: narrowing 4294967297 to int would wrap to 1 and pass.
        if (tickets > MaxTicketsPerOrder)
            return $"An order takes at most {MaxTicketsPerOrder} tickets.";
        return null;
    }

    /// <summary>Case-sensitive substring match: a longer token that merely contains the poison matches too.</summary>
    public static bool IsPoison(string reference) => reference.Contains(PoisonToken, StringComparison.Ordinal);

    /// <summary>Face value plus a 12% service fee truncated to a whole cent, in cents. Overflow throws instead of wrapping.</summary>
    public static long TotalCents(long priceCents, int tickets)
    {
        var face = checked(priceCents * tickets);
        return checked(face + face * ServiceFeeBasisPoints / 10_000);
    }
}
