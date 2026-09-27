using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>
/// WARD-14/WARD-15 read models. Deliberately separate from <see cref="IFinanceRepository"/>:
/// a ward's dashboard spans slots, registrations and applications, none of which the finance
/// module owns, so a report repository composing its own reads across all of them (the same
/// shape <c>Infrastructure/Services/WardSlots.cs</c> already uses for ward-facing views) is a
/// cleaner boundary than growing IFinanceRepository into a second thing it is not.
/// </summary>
public interface IWardReportRepository
{
    /// <summary>WARD-14: totals for the ward between two dates (inclusive), plus its 10 most recent violations.</summary>
    Task<CollectionReportRow> GetCollectionReportAsync(
        int wardUnitId, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>WARD-15: the ward's current operational snapshot.</summary>
    Task<WardDashboardRow> GetDashboardAsync(int wardUnitId, CancellationToken cancellationToken);
}
