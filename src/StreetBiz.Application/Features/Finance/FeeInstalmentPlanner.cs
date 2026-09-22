using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Sidewalk;

namespace StreetBiz.Application.Features.Finance;

/// <summary>
/// SYS-03. Splits a rental term into instalments. The total is never recomputed here: it comes
/// from <see cref="SidewalkSlots.GetSlotQuote.FeeQuoteCalculator"/>, the same calculator behind
/// the SIDE-02 quote, so an approved contract can never cost more than what the vendor was quoted
/// for that slot and term.
///
/// BR-18 fixes the inputs (start date + vendor-selected term) but leaves the cadence open. The
/// rule chosen for this project: instalments cover <see cref="PeriodDays"/>-day periods counted
/// from the contract start date; PER_TERM components (admin fee, deposit) fall entirely on the
/// first instalment; the final instalment absorbs rounding so the instalments always sum to the
/// total that FeeSchedules.total_amount stores.
/// </summary>
public static class FeeInstalmentPlanner
{
    public const int PeriodDays = 30;

    public static FeePlan Plan(FeeQuoteDto quote, DateOnly startDate)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (quote.TermDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quote), "Rental term must be at least one day.");
        }

        var termDays = quote.TermDays;
        var dailyRate = quote.Lines.Where(line => line.CalcBasis == FeeBases.PerDay).Sum(line => line.UnitAmount);
        var oneOff = quote.Lines.Where(line => line.CalcBasis == FeeBases.PerTerm).Sum(line => line.UnitAmount);
        var total = Whole(quote.Total);

        var count = (termDays + PeriodDays - 1) / PeriodDays;
        var instalments = new List<FeeInstalment>(count);
        var allocated = 0m;

        for (var ordinal = 1; ordinal <= count; ordinal++)
        {
            var offset = (ordinal - 1) * PeriodDays;
            var daysInPeriod = Math.Min(PeriodDays, termDays - offset);
            var periodStart = startDate.AddDays(offset);

            var amount = ordinal == count
                // The last instalment is the remainder, so rounding can never leave the
                // instalments disagreeing with the stored total.
                ? total - allocated
                : Whole(dailyRate * daysInPeriod) + (ordinal == 1 ? Whole(oneOff) : 0m);

            allocated += amount;
            instalments.Add(new FeeInstalment(
                ordinal,
                periodStart,
                periodStart,
                startDate.AddDays(offset + daysInPeriod - 1),
                amount));
        }

        return new FeePlan(total, instalments);
    }

    /// <summary>Fees are stored as DECIMAL(18,0): every amount written must be a whole dong.</summary>
    private static decimal Whole(decimal value) => decimal.Round(value, 0, MidpointRounding.AwayFromZero);
}
