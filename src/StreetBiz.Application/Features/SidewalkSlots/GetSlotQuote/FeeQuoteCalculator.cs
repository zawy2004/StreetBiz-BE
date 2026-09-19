using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Sidewalk;

namespace StreetBiz.Application.Features.SidewalkSlots.GetSlotQuote;

/// <summary>
/// Estimates what a term costs: the zone's daily price times the term, plus each fee component
/// (PER_DAY components times the term, PER_TERM components once). The real fee schedule is
/// generated later (WARD-08), so this is informational and never stored.
/// </summary>
public static class FeeQuoteCalculator
{
    public static FeeQuoteDto Calculate(
        long slotId, decimal pricePerDay, int termDays, IReadOnlyList<FeeComponentRow> components)
    {
        var lines = new List<FeeQuoteLineDto>
        {
            new("RENT", null, FeeBases.PerDay, pricePerDay, termDays, pricePerDay * termDays),
        };

        foreach (var component in components)
        {
            var quantity = component.CalcBasis == FeeBases.PerDay ? termDays : 1;
            lines.Add(new FeeQuoteLineDto(
                "FEE", component.ComponentName, component.CalcBasis, component.UnitAmount, quantity,
                component.UnitAmount * quantity));
        }

        return new FeeQuoteDto(slotId, termDays, lines, lines.Sum(l => l.Amount));
    }
}
