using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Sidewalk;

namespace StreetBiz.Application.Features.Finance;

/// <summary>
/// SYS-03. Splits a rental term into instalments.
///
/// What is billed is the rent, price_per_day x days, and nothing else: that is the "tiền thuê tính
/// vào hợp đồng" line of the SIDE-02 quote (<see cref="FeeQuoteDto.BaseFee"/>). The zone's other
/// components (cleaning, admin fee, deposit) are shown to the vendor as reference only and "chưa
/// tính vào hợp đồng" (WARD-01/02), so charging them here would bill more than the vendor agreed to.
///
/// Cadence (BR-18 leaves it open): <see cref="PeriodDays"/>-day periods counted from the first day
/// billed, each due on the day its period starts, the last absorbing rounding so the instalments
/// always sum to the stored total. No instalment is ever due sooner than <see cref="GraceDays"/>
/// after it was issued: a contract approved today, or a schedule generated late, does not open
/// with an instalment that is overdue tomorrow.
/// </summary>
public static class FeeInstalmentPlanner
{
    public const int PeriodDays = 30;
    public const int GraceDays = 7;

    /// <summary>The whole term of a newly approved contract, priced from its SIDE-02 quote.</summary>
    public static FeePlan Plan(FeeQuoteDto quote, DateOnly startDate, DateOnly issuedOn)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (quote.TermDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quote), "Rental term must be at least one day.");
        }

        var dailyRent = quote.Lines.Where(line => line.Kind == FeeQuoteLineKinds.Rent).Sum(line => line.UnitAmount);
        return Split(dailyRent, startDate, quote.TermDays, issuedOn);
    }

    /// <summary>
    /// WARD-09: the days a renewal adds, from <paramref name="firstDay"/> to <paramref name="lastDay"/>
    /// inclusive, at the zone's rent on the day the renewal is approved.
    /// </summary>
    public static FeePlan PlanExtension(decimal pricePerDay, DateOnly firstDay, DateOnly lastDay, DateOnly issuedOn)
    {
        if (lastDay < firstDay)
        {
            throw new ArgumentOutOfRangeException(nameof(lastDay), "A renewal must add at least one day.");
        }

        return Split(pricePerDay, firstDay, lastDay.DayNumber - firstDay.DayNumber + 1, issuedOn);
    }

    private static FeePlan Split(decimal dailyRent, DateOnly firstDay, int days, DateOnly issuedOn)
    {
        var total = Whole(dailyRent * days);
        var earliestDue = issuedOn.AddDays(GraceDays);
        var count = (days + PeriodDays - 1) / PeriodDays;
        var instalments = new List<FeeInstalment>(count);
        var allocated = 0m;

        for (var ordinal = 1; ordinal <= count; ordinal++)
        {
            var offset = (ordinal - 1) * PeriodDays;
            var daysInPeriod = Math.Min(PeriodDays, days - offset);
            var periodStart = firstDay.AddDays(offset);

            // The last instalment is the remainder, so rounding can never leave the instalments
            // disagreeing with the stored total.
            var amount = ordinal == count ? total - allocated : Whole(dailyRent * daysInPeriod);
            allocated += amount;

            instalments.Add(new FeeInstalment(
                ordinal,
                periodStart < earliestDue ? earliestDue : periodStart,
                periodStart,
                firstDay.AddDays(offset + daysInPeriod - 1),
                amount));
        }

        return new FeePlan(total, instalments);
    }

    /// <summary>Fees are stored as DECIMAL(18,0): every amount written must be a whole dong.</summary>
    private static decimal Whole(decimal value) => decimal.Round(value, 0, MidpointRounding.AwayFromZero);
}

/// <summary><see cref="FeeQuoteLineDto.Kind"/> values written by FeeQuoteCalculator.</summary>
public static class FeeQuoteLineKinds
{
    public const string Rent = "RENT";
    public const string ReferenceFee = "FEE";
}
