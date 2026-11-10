using System.Security.Cryptography;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>
/// The order number a customer reads to the stall and a seller reads off a ticket, e.g.
/// <c>DH-261103-K7Q4MX</c>: "DH" (đơn hàng, as in the demo orders), the order's date in Vietnam
/// and six characters. The old code was the date plus 17 hex digits of a GUID, which nobody
/// could read aloud. The alphabet leaves out 0/O, 1/I/L and U so a code survives being said
/// over a counter or copied by hand.
/// </summary>
public static class OrderCodes
{
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int RandomLength = 6;

    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    /// <param name="nowUtc">When the order is placed.</param>
    /// <param name="next">Picks an index below its argument; the default draws from a cryptographic RNG.</param>
    public static string New(DateTime nowUtc, Func<int, int>? next = null)
    {
        next ??= RandomNumberGenerator.GetInt32;
        var local = nowUtc.Add(VietnamOffset);
        Span<char> random = stackalloc char[RandomLength];
        for (var i = 0; i < random.Length; i++)
        {
            random[i] = Alphabet[next(Alphabet.Length)];
        }

        return $"DH-{local:yyMMdd}-{random}";
    }
}
