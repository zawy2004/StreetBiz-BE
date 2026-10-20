using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Sidewalk;

namespace StreetBiz.Application.Features.SidewalkSlots.GetSlotQuote;

/// <summary>
/// Prices a term: the rent, any quarterly discount, then each fee component (PER_DAY components
/// times the term, PER_TERM once per term, ONE_TIME and DEPOSIT once on the first contract only).
/// The same calculator produces the vendor's quote, the approved contract's fee schedule and the
/// officer's price-change impact preview, so the three can never disagree.
/// </summary>
public static class FeeQuoteCalculator
{
    /// <summary>Day-by-day pricing with no discount, as quoted before monthly billing existed.</summary>
    public static FeeQuoteDto Calculate(
        long slotId, decimal pricePerDay, int termDays, IReadOnlyList<FeeComponentRow> components) =>
        Calculate(slotId, ZonePricing.Daily(pricePerDay), termDays, components);

    public static FeeQuoteDto Calculate(
        long slotId,
        ZonePricing pricing,
        int termDays,
        IReadOnlyList<FeeComponentRow> components,
        bool isRenewal = false)
    {
        var lines = new List<FeeQuoteLineDto> { RentLine(pricing, termDays) };

        // A whole number of quarters earns the zone's discount, on the rent only.
        if (pricing.BillsByMonth
            && pricing.QuarterDiscountPercent is decimal percent and > 0m
            && termDays % RentalTerms.QuarterDays == 0)
        {
            var discount = decimal.Round(lines[0].Amount * percent / 100m, 0, MidpointRounding.AwayFromZero);
            lines.Add(new FeeQuoteLineDto(
                "DISCOUNT", $"Khuyến mãi thuê theo quý (-{percent:0.##}%)", lines[0].CalcBasis,
                -discount, 1, -discount));
        }

        foreach (var component in components)
        {
            if (isRenewal && component.CalcBasis is FeeBases.OneTime or FeeBases.Deposit)
            {
                continue;
            }

            var quantity = component.CalcBasis == FeeBases.PerDay ? termDays : 1;
            lines.Add(new FeeQuoteLineDto(
                component.CalcBasis == FeeBases.Deposit ? "DEPOSIT" : "FEE",
                component.ComponentName, component.CalcBasis, component.UnitAmount, quantity,
                component.UnitAmount * quantity));
        }

        var baseFee = lines.Where(l => l.Kind is "RENT" or "DISCOUNT").Sum(l => l.Amount);
        var referenceFees = lines.Where(l => l.Kind == "FEE").Sum(l => l.Amount);
        var deposit = lines.Where(l => l.Kind == "DEPOSIT").Sum(l => l.Amount);
        return new FeeQuoteDto(
            slotId, termDays, lines, baseFee + referenceFees + deposit, baseFee, referenceFees,
            IsReferenceOnly: false, deposit);
    }

    /// <summary>A STANDARD zone's whole-month term is billed by the month; anything else by the day.</summary>
    private static FeeQuoteLineDto RentLine(ZonePricing pricing, int termDays)
    {
        if (pricing.BillsByMonth && termDays % RentalTerms.MonthDays == 0)
        {
            var months = termDays / RentalTerms.MonthDays;
            return new FeeQuoteLineDto(
                "RENT", null, FeeBases.PerMonth, pricing.MonthlyPrice, months, pricing.MonthlyPrice * months);
        }

        return new FeeQuoteLineDto(
            "RENT", null, FeeBases.PerDay, pricing.PricePerDay, termDays, pricing.PricePerDay * termDays);
    }
}
