using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Infrastructure.Persistence;

/// <summary>
/// Development only, run by scripts/setup-local-db.ps1 (<c>dotnet run -- --sign-demo-permits</c>).
/// The demo seed cannot ship a scannable permit QR: the token is signed with Permit:SigningKey,
/// which is different on every machine. This signs the seed's placeholders (SEED-PERMIT-…) with
/// this machine's key through the real <see cref="IPermitTokenService"/>, so a freshly built
/// database has permits that verify. Nothing else changes: a real token is never replaced, and
/// the permit's status and dates stay as seeded.
/// </summary>
public static class DemoPermits
{
    public const string CommandLineSwitch = "--sign-demo-permits";
    public const string PlaceholderPrefix = "SEED-PERMIT-";

    /// <summary>Signs every placeholder permit; returns how many. Running it again signs none.</summary>
    public static async Task<int> SignPlaceholdersAsync(
        StreetBizDbContext db,
        IPermitTokenService tokens,
        CancellationToken cancellationToken)
    {
        var permits = await db.DigitalPermits
            .Where(permit => permit.qr_payload.StartsWith(PlaceholderPrefix))
            .ToListAsync(cancellationToken);
        foreach (var permit in permits)
        {
            permit.qr_payload = tokens.Create(permit.contract_id, DateTime.SpecifyKind(permit.issued_at, DateTimeKind.Utc));
        }

        await db.SaveChangesAsync(cancellationToken);
        return permits.Count;
    }
}
