using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace StreetBiz.Infrastructure.Persistence;

/// <summary>
/// Every DATETIME2 column holds UTC (written from <c>clock.GetUtcNow().UtcDateTime</c> or
/// SYSUTCDATETIME()). SQL Server keeps no kind, so EF read them back as
/// <see cref="DateTimeKind.Unspecified"/> and the API wrote them without a zone
/// ("2026-10-19T17:40:12"); clients then read that as local time and showed it 7 hours early.
/// Reading them as <see cref="DateTimeKind.Utc"/> makes the JSON carry "Z", so every client
/// converts to Asia/Ho_Chi_Minh correctly. Values are written unchanged (a local-kind value,
/// should one ever appear, is converted to UTC first).
/// </summary>
public partial class StreetBizDbContext
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }
}

/// <summary>Stored as is; read back marked as UTC.</summary>
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

/// <summary>Nullable form of <see cref="UtcDateTimeConverter"/>.</summary>
public sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v.HasValue && v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : v,
    v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
