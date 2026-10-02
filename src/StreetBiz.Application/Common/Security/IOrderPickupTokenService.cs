namespace StreetBiz.Application.Common.Security;

/// <summary>What a scanned pickup QR claims. Nothing here is trusted until the
/// order itself is loaded and checked against it.</summary>
public sealed record OrderPickupTokenClaims(long OrderId, long CustomerUserId);

/// <summary>
/// ORD-06: the QR a paid customer shows at the stall. The seller scans it to
/// prove that this customer, holding this order, is the one collecting - rather
/// than taking their word for it and pressing a button.
/// </summary>
public interface IOrderPickupTokenService
{
    /// <summary>
    /// Deterministic for a given order, so the customer sees the same code every
    /// time they reopen the screen.
    /// </summary>
    string Create(long orderId, long customerUserId);

    bool TryParse(string token, out OrderPickupTokenClaims claims);

    /// <summary>
    /// The same proof in a form a person can read out and type, for when the
    /// seller's camera is broken or their browser cannot decode a QR at all.
    /// Deterministic per order, like <see cref="Create"/>.
    /// </summary>
    string CreateShortCode(long orderId, long customerUserId);

    /// <summary>
    /// Cleans up what a seller typed - case, spacing and the characters people
    /// habitually confuse - so a correct code is not rejected over presentation.
    /// Returns null when nothing usable is left.
    /// </summary>
    string? NormaliseShortCode(string? typed);

    /// <summary>Constant-time comparison of a typed code against an order's own.</summary>
    bool ShortCodeMatches(string normalisedCode, long orderId, long customerUserId);
}
