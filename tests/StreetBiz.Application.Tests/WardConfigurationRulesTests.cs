using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.WardConfiguration;

namespace StreetBiz.Application.Tests;

public sealed class WardConfigurationRulesTests
{
    [Fact]
    public void Legal_basis_bracket_round_trips_through_its_text_form()
    {
        var text = LegalBasisText.Format("Nghị định 168/2024/NĐ-CP", "12", "5", null, "kinh doanh trái phép trên vỉa hè", 2_000_000, 3_000_000);

        Assert.Equal("Nghị định 168/2024/NĐ-CP, Điều 12, khoản 5: kinh doanh trái phép trên vỉa hè (khung 2.000.000 - 3.000.000đ)", text);
        Assert.Equal((2_000_000L, 3_000_000L), LegalBasisText.TryParseBracket(text));
    }

    [Fact]
    public void Brackets_written_by_the_demo_seed_before_WARD_03_still_parse()
    {
        const string seed = "Nghị định 168/2024/NĐ-CP: bán hàng rong tại tuyến phố cấm (khung 200.000 - 250.000đ)";
        Assert.Equal((200_000L, 250_000L), LegalBasisText.TryParseBracket(seed));
        Assert.Null(LegalBasisText.TryParseBracket("Nghị định 168/2024/NĐ-CP"));
        Assert.Null(LegalBasisText.TryParseBracket(null));
    }

    [Theory]
    [InlineData(2_000_000, 3_000_000, 2_500_000)]
    [InlineData(200_000, 250_000, 225_000)]
    [InlineData(100_001, 100_002, 100_001)]
    public void Default_fine_is_the_bracket_midpoint_rounded_down(long min, long max, long expected) =>
        Assert.Equal(expected, LegalBasisText.Midpoint(min, max));

    [Fact]
    public void Penalty_brackets_outside_the_statutory_range_are_rejected()
    {
        var validator = new SetWardPenaltyRateCommandValidator();
        Assert.False(validator.Validate(Penalty(10_000, 20_000)).IsValid);
        Assert.False(validator.Validate(Penalty(3_000_000, 2_000_000)).IsValid);
        Assert.True(validator.Validate(Penalty(2_000_000, 3_000_000)).IsValid);
    }

    [Fact]
    public void Overnight_hours_are_valid_but_identical_or_half_set_hours_are_not()
    {
        var validator = new CreateWardZoneCommandValidator();
        Assert.True(validator.Validate(Zone(new TimeOnly(18, 0), new TimeOnly(2, 0))).IsValid);
        Assert.True(ZoneHours.IsOvernight(new TimeOnly(18, 0), new TimeOnly(2, 0)));
        Assert.False(validator.Validate(Zone(new TimeOnly(8, 0), new TimeOnly(8, 0))).IsValid);
        Assert.False(validator.Validate(Zone(new TimeOnly(8, 0), null)).IsValid);
    }

    [Fact]
    public void A_new_zone_needs_every_part_of_its_permitting_document()
    {
        var validator = new CreateWardZoneCommandValidator();
        var missingIssuer = Zone(null, null) with
        {
            Request = Zone(null, null).Request with { RegulationIssuer = null },
        };
        Assert.False(validator.Validate(missingIssuer).IsValid);

        var noDocument = Zone(null, null) with
        {
            Request = Zone(null, null).Request with { RegulationNumber = null, RegulationIssuedOn = null, RegulationIssuer = null },
        };
        Assert.False(validator.Validate(noDocument).IsValid);
    }

    [Fact]
    public void Updating_a_zone_requires_a_reason_and_a_version_token()
    {
        var request = Zone(null, null).Request;
        var validator = new UpdateWardZoneCommandValidator();
        Assert.False(validator.Validate(new UpdateWardZoneCommand(1, request)).IsValid);
        Assert.True(validator.Validate(new UpdateWardZoneCommand(1, request with { ChangeReason = "Điều chỉnh giá", VersionToken = "abc" })).IsValid);
    }

    private static SetWardPenaltyRateCommand Penalty(long min, long max) =>
        new(new SetPenaltyRateRequest("UNAUTHORIZED_BUSINESS_USE", "Nghị định 168/2024/NĐ-CP", "12", "5", null,
            "kinh doanh trái phép", min, max, new DateOnly(2026, 10, 1), null));

    private static CreateWardZoneCommand Zone(TimeOnly? from, TimeOnly? to) =>
        new(new UpsertZoneRequest("Đường Bạch Đằng", "HC1-BD", 40_000, from, to, "QĐ 15/QĐ-UBND", new DateOnly(2026, 9, 1),
            "UBND phường Hải Châu", null, null, null, [], null, null));

    [Fact]
    public void A_monthly_priced_zone_needs_a_positive_monthly_price_not_a_daily_one()
    {
        var validator = new CreateWardZoneCommandValidator();
        var monthly = Zone(null, null) with
        {
            Request = Zone(null, null).Request with { PriceDisplayUnit = PriceDisplayUnits.Month, PricePerDay = 0, PricePerMonth = 900_000 },
        };
        Assert.True(validator.Validate(monthly).IsValid);

        var missingMonthlyPrice = Zone(null, null) with
        {
            Request = Zone(null, null).Request with { PriceDisplayUnit = PriceDisplayUnits.Month, PricePerDay = 0, PricePerMonth = null },
        };
        Assert.False(validator.Validate(missingMonthlyPrice).IsValid);
    }

    [Fact]
    public void An_event_zone_cannot_be_priced_monthly_and_needs_coherent_event_dates()
    {
        var validator = new CreateWardZoneCommandValidator();
        var eventZone = Zone(null, null) with
        {
            Request = Zone(null, null).Request with
            {
                RentalMode = RentalModes.Event,
                EventStartDate = new DateOnly(2026, 12, 1),
                EventEndDate = new DateOnly(2026, 12, 10),
            },
        };
        Assert.True(validator.Validate(eventZone).IsValid);

        var eventZoneMonthlyPriced = eventZone with
        {
            Request = eventZone.Request with { PriceDisplayUnit = PriceDisplayUnits.Month, PricePerMonth = 900_000 },
        };
        Assert.False(validator.Validate(eventZoneMonthlyPriced).IsValid);

        var eventZoneNoDates = eventZone with
        {
            Request = eventZone.Request with { EventStartDate = null, EventEndDate = null },
        };
        Assert.False(validator.Validate(eventZoneNoDates).IsValid);

        var eventZoneBackwardsDates = eventZone with
        {
            Request = eventZone.Request with { EventStartDate = new DateOnly(2026, 12, 10), EventEndDate = new DateOnly(2026, 12, 1) },
        };
        Assert.False(validator.Validate(eventZoneBackwardsDates).IsValid);
    }
}
