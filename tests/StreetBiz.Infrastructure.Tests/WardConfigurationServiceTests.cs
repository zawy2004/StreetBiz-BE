using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Features.WardCompliance;
using StreetBiz.Application.Features.WardConfiguration;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Security;
using StreetBiz.Infrastructure.Services;
using StreetBiz.Infrastructure.Sidewalk;

namespace StreetBiz.Infrastructure.Tests;

public sealed class WardConfigurationServiceTests
{
    // 10:00 on 24/09/2026 in Vietnam.
    private static readonly DateTimeOffset DefaultNow = new(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 24);

    #region WARD-03
    [Fact]
    public async Task Setting_a_rate_stores_the_bracket_midpoint_and_formatted_legal_basis()
    {
        using var f = await Fixture.Create();
        var result = await f.Service().SetPenaltyRateAsync(f.Actor, Rate("HYGIENE_LITTERING", 1_000_000, 2_000_000, Today, null), default);

        Assert.Equal(1_500_000m, result.Current!.Amount);
        Assert.Equal(1_000_000, result.Current.BracketMin);
        Assert.Equal(2_000_000, result.Current.BracketMax);
        Assert.Equal("Nghị định 45/2022/NĐ-CP, Điều 25, khoản 2, điểm d: vứt, thải, bỏ rác thải trên vỉa hè (khung 1.000.000 - 2.000.000đ)",
            result.Current.LegalBasis);
        Assert.True(result.HasLegalBasis);
        Assert.Equal(1, await f.AuditCount("PENALTY_RATE_SET"));

        var history = await f.Service().ListPenaltyHistoryAsync(f.Actor, "HYGIENE_LITTERING", default);
        // The seeded UserAccounts.full_name for user_id 1, not f.Actor.Name (a separate test double).
        Assert.Equal("Cán bộ 1", Assert.Single(history).ActorName);
    }

    [Fact]
    public async Task A_future_rate_is_scheduled_and_the_current_rate_stays_in_force_until_then()
    {
        using var f = await Fixture.Create();
        var service = f.Service();
        var result = await service.SetPenaltyRateAsync(f.Actor,
            Rate("UNAUTHORIZED_BUSINESS_USE", 3_000_000, 4_000_000, Today.AddDays(7), expected: 1), default);

        Assert.Equal(1, result.Current!.ScheduleId);
        Assert.Equal(Today.AddDays(7), result.Current.EffectiveTo);
        Assert.Equal(3_500_000m, result.Scheduled!.Amount);

        // WARD-13 lookup still returns the old rate today and the new one from its start date.
        var compliance = f.Compliance();
        Assert.Equal(2_500_000m, Assert.Single(await compliance.ListPenaltySchedulesAsync(f.Actor, null, default)).PenaltyAmount);
        Assert.Equal(3_500_000m, Assert.Single(await compliance.ListPenaltySchedulesAsync(f.Actor, Today.AddDays(7), default)).PenaltyAmount);
    }

    [Fact]
    public async Task A_second_rate_cannot_be_set_while_one_is_already_scheduled()
    {
        using var f = await Fixture.Create();
        var service = f.Service();
        var scheduled = await service.SetPenaltyRateAsync(f.Actor,
            Rate("UNAUTHORIZED_BUSINESS_USE", 3_000_000, 4_000_000, Today.AddDays(7), expected: 1), default);

        var ex = await Assert.ThrowsAsync<WardException>(() => service.SetPenaltyRateAsync(f.Actor,
            Rate("UNAUTHORIZED_BUSINESS_USE", 3_000_000, 5_000_000, Today.AddDays(9), expected: scheduled.Scheduled!.ScheduleId), default));
        Assert.Equal("rate_already_scheduled", ex.Code);
    }

    [Fact]
    public async Task Cancelling_a_scheduled_rate_reopens_the_previous_one()
    {
        using var f = await Fixture.Create();
        var service = f.Service();
        var scheduled = await service.SetPenaltyRateAsync(f.Actor,
            Rate("UNAUTHORIZED_BUSINESS_USE", 3_000_000, 4_000_000, Today.AddDays(7), expected: 1), default);

        var result = await service.CancelScheduledPenaltyRateAsync(f.Actor, scheduled.Scheduled!.ScheduleId, default);

        Assert.Null(result.Scheduled);
        Assert.Equal(1, result.Current!.ScheduleId);
        Assert.Null(result.Current.EffectiveTo);
        Assert.Equal(1, await f.AuditCount("PENALTY_RATE_CANCELLED"));
    }

    [Fact]
    public async Task Cancelling_a_rate_already_in_force_is_rejected()
    {
        using var f = await Fixture.Create();
        var ex = await Assert.ThrowsAsync<WardException>(() => f.Service().CancelScheduledPenaltyRateAsync(f.Actor, 1, default));
        Assert.Equal("rate_not_scheduled", ex.Code);
    }

    [Fact]
    public async Task A_stale_expected_current_rate_is_a_conflict()
    {
        using var f = await Fixture.Create();
        await Assert.ThrowsAsync<ConflictException>(() => f.Service().SetPenaltyRateAsync(f.Actor,
            Rate("UNAUTHORIZED_BUSINESS_USE", 3_000_000, 4_000_000, Today.AddDays(1), expected: null), default));
    }

    [Fact]
    public async Task A_same_day_correction_edits_the_unused_row_instead_of_breaking_the_date_check()
    {
        using var f = await Fixture.Create();
        var service = f.Service();
        var first = await service.SetPenaltyRateAsync(f.Actor, Rate("HYGIENE_LITTERING", 1_000_000, 2_000_000, Today, null), default);

        var second = await service.SetPenaltyRateAsync(f.Actor,
            Rate("HYGIENE_LITTERING", 1_000_000, 3_000_000, Today, first.Current!.ScheduleId), default);

        Assert.Equal(first.Current.ScheduleId, second.Current!.ScheduleId);
        Assert.Equal(2_000_000m, second.Current.Amount);
        Assert.Equal(1, await f.AuditCount("PENALTY_RATE_REVISED"));
    }

    [Fact]
    public async Task A_same_day_correction_is_refused_once_a_penalty_uses_the_row()
    {
        using var f = await Fixture.Create();
        var service = f.Service();
        var first = await service.SetPenaltyRateAsync(f.Actor, Rate("HYGIENE_LITTERING", 1_000_000, 2_000_000, Today, null), default);
        await f.AddPenaltyUsing(first.Current!.ScheduleId, "HYGIENE_LITTERING");

        var ex = await Assert.ThrowsAsync<WardException>(() => service.SetPenaltyRateAsync(f.Actor,
            Rate("HYGIENE_LITTERING", 1_000_000, 3_000_000, Today, first.Current.ScheduleId), default));
        Assert.Equal("rate_same_day_in_use", ex.Code);
    }

    [Fact]
    public async Task Retired_violation_types_cannot_get_a_new_rate()
    {
        using var f = await Fixture.Create();
        var ex = await Assert.ThrowsAsync<WardException>(() =>
            f.Service().SetPenaltyRateAsync(f.Actor, Rate("NO_PERMIT", 2_000_000, 3_000_000, Today, null), default));
        Assert.Equal("violation_type_inactive", ex.Code);
    }

    [Fact]
    public async Task Today_is_the_Vietnam_date_not_the_UTC_date()
    {
        // 00:30 on 25/09 in Vietnam is still 24/09 in UTC.
        using var f = await Fixture.Create(now: new DateTimeOffset(2026, 9, 24, 17, 30, 0, TimeSpan.Zero));
        var result = await f.Service().SetPenaltyRateAsync(f.Actor, Rate("HYGIENE_LITTERING", 1_000_000, 2_000_000, new DateOnly(2026, 9, 25), null), default);
        Assert.NotNull(result.Current);

        var ex = await Assert.ThrowsAsync<WardException>(() => f.Service().SetPenaltyRateAsync(f.Actor,
            Rate("UNAUTHORIZED_BUSINESS_USE", 1_000_000, 2_000_000, new DateOnly(2026, 9, 24), 1), default));
        Assert.Equal("effective_date_in_past", ex.Code);
    }

    [Fact]
    public async Task Overview_flags_types_without_a_legal_basis()
    {
        using var f = await Fixture.Create();
        var overview = await f.Service().ListPenaltyOverviewAsync(f.Actor, default);

        Assert.True(overview.Single(t => t.ViolationType == "UNAUTHORIZED_BUSINESS_USE").HasLegalBasis);
        Assert.False(overview.Single(t => t.ViolationType == "HYGIENE_LITTERING").HasLegalBasis);
        Assert.False(overview.Single(t => t.ViolationType == "NO_PERMIT").IsActive);
    }

    [Fact]
    public async Task Another_wards_rate_cannot_be_cancelled()
    {
        using var f = await Fixture.Create();
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service().CancelScheduledPenaltyRateAsync(f.OtherActor, 1, default));
    }
    #endregion

    #region WARD-13 sanction fixes
    [Fact]
    public async Task Sanctioning_with_a_rate_for_a_different_violation_type_is_a_conflict()
    {
        using var f = await Fixture.Create();
        var hygiene = await f.Service().SetPenaltyRateAsync(f.Actor, Rate("HYGIENE_LITTERING", 1_000_000, 2_000_000, Today, null), default);
        var violationId = await f.RecordViolation("UNAUTHORIZED_BUSINESS_USE");

        await Assert.ThrowsAsync<ConflictException>(() => f.Compliance().SanctionViolationAsync(f.Actor, violationId,
            Sanction(hygiene.Current!.ScheduleId), default));
    }

    [Fact]
    public async Task Sanctioning_with_a_rate_not_in_force_on_the_violation_date_is_a_conflict()
    {
        using var f = await Fixture.Create();
        var violationId = await f.RecordViolation("UNAUTHORIZED_BUSINESS_USE");
        var scheduled = await f.Service().SetPenaltyRateAsync(f.Actor,
            Rate("UNAUTHORIZED_BUSINESS_USE", 3_000_000, 4_000_000, Today.AddDays(7), expected: 1), default);

        await Assert.ThrowsAsync<ConflictException>(() => f.Compliance().SanctionViolationAsync(f.Actor, violationId,
            Sanction(scheduled.Scheduled!.ScheduleId), default));

        var ok = await f.Compliance().SanctionViolationAsync(f.Actor, violationId, Sanction(1), default);
        Assert.Equal(2_500_000m, ok.PenaltyAmount);
    }

    [Fact]
    public async Task Sanctioning_under_a_rate_without_legal_basis_is_blocked()
    {
        using var f = await Fixture.Create();
        await f.AddRate(9, "HYGIENE_LITTERING", 750_000, legalBasis: null);
        var violationId = await f.RecordViolation("HYGIENE_LITTERING");

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            f.Compliance().SanctionViolationAsync(f.Actor, violationId, Sanction(9), default));
        Assert.Contains("chưa có căn cứ pháp lý", ex.Message);
    }

    [Fact]
    public async Task Another_ward_cannot_read_or_sanction_this_wards_violation()
    {
        using var f = await Fixture.Create();
        var violationId = await f.RecordViolation("UNAUTHORIZED_BUSINESS_USE");

        await Assert.ThrowsAsync<NotFoundException>(() => f.Compliance().GetViolationDetailAsync(f.OtherActor, violationId, default));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Compliance().SanctionViolationAsync(f.OtherActor, violationId, Sanction(1), default));
        Assert.Empty(await f.Compliance().ListViolationsAsync(f.OtherActor, null, 1, default));
    }

    [Fact]
    public async Task Recording_a_retired_type_or_another_wards_slot_is_refused()
    {
        using var f = await Fixture.Create();
        var ex = await Assert.ThrowsAsync<WardException>(() => f.Compliance().RecordViolationAsync(f.Actor,
            new RecordWardViolationRequestFactory().For(100, "NO_PERMIT"), default));
        Assert.Equal("violation_type_inactive", ex.Code);

        await Assert.ThrowsAsync<NotFoundException>(() => f.Compliance().RecordViolationAsync(f.OtherActor,
            new RecordWardViolationRequestFactory().For(100, "UNAUTHORIZED_BUSINESS_USE"), default));
    }

    [Fact]
    public async Task Retired_types_are_not_offered_when_recording()
    {
        using var f = await Fixture.Create();
        await f.AddRate(9, "NO_PERMIT", 2_000_000, legalBasis: "x");
        var schedules = await f.Compliance().ListPenaltySchedulesAsync(f.Actor, null, default);
        Assert.DoesNotContain(schedules, s => s.ViolationType == "NO_PERMIT");
    }
    #endregion

    #region WARD-02
    [Fact]
    public async Task Creating_a_zone_composes_the_regulation_reference_and_audits_once()
    {
        using var f = await Fixture.Create();
        var zone = await f.Service().CreateZoneAsync(f.Actor, ZoneRequest("Đường Bạch Đằng", "HC1-BD", 40_000), default);

        Assert.Equal("QĐ 15/QĐ-UBND ngày 01/09/2026 của UBND phường Hải Châu", zone.RegulationRef);
        Assert.Equal(2, zone.FeeComponents.Count);
        Assert.Equal(1, await f.AuditCount("ZONE_CREATED"));
    }

    [Fact]
    public async Task A_zone_name_already_used_in_the_ward_is_rejected()
    {
        using var f = await Fixture.Create();
        var ex = await Assert.ThrowsAsync<WardException>(() =>
            f.Service().CreateZoneAsync(f.Actor, ZoneRequest("Đường Nguyễn Văn Linh", "HC1-X", 40_000), default));
        Assert.Equal("zone_name_taken", ex.Code);
    }

    [Fact]
    public async Task Updating_a_zone_with_a_stale_version_token_is_a_conflict()
    {
        using var f = await Fixture.Create();
        var zone = await f.Service().GetZoneAsync(f.Actor, 1, default);
        await f.Service().UpdateZoneAsync(f.Actor, 1, Edit(zone, price: 35_000), default);

        await Assert.ThrowsAsync<ConflictException>(() =>
            f.Service().UpdateZoneAsync(f.Actor, 1, Edit(zone, price: 36_000), default));
    }

    [Fact]
    public async Task Fee_component_edits_change_the_version_token()
    {
        using var f = await Fixture.Create();
        var zone = await f.Service().GetZoneAsync(f.Actor, 1, default);
        await f.Service().UpdateZoneAsync(f.Actor, 1, Edit(zone, price: 30_000) with
        {
            FeeComponents = [new ZoneFeeComponentInput("Phí vệ sinh", "PER_DAY", 9_000)],
        }, default);

        // Same price, but the fee list changed underneath: the old token must no longer match.
        await Assert.ThrowsAsync<ConflictException>(() =>
            f.Service().UpdateZoneAsync(f.Actor, 1, Edit(zone, price: 30_000), default));
    }

    [Fact]
    public async Task A_monthly_price_is_derived_into_price_per_day_which_the_fee_engine_keeps_reading()
    {
        using var f = await Fixture.Create();
        var zone = await f.Service().GetZoneAsync(f.Actor, 1, default);
        var monthly = Edit(zone, price: 0) with { PriceDisplayUnit = "MONTH", PricePerMonth = 900_000 };

        var updated = await f.Service().UpdateZoneAsync(f.Actor, 1, monthly, default);

        Assert.Equal("MONTH", updated.PriceDisplayUnit);
        Assert.Equal(900_000m, updated.PricePerMonth);
        // 900,000 / 30 -- the only value FeeQuoteCalculator/FeeInstalmentPlanner ever read.
        Assert.Equal(30_000m, updated.PricePerDay);
    }

    [Fact]
    public async Task Creating_an_event_zone_stores_its_window_and_keeps_daily_pricing()
    {
        using var f = await Fixture.Create();
        var request = ZoneRequest("Hội chợ Tết", "HC1-TET", 50_000) with
        {
            RentalMode = "EVENT",
            EventStartDate = new DateOnly(2026, 12, 20),
            EventEndDate = new DateOnly(2026, 12, 28),
        };

        var zone = await f.Service().CreateZoneAsync(f.Actor, request, default);

        Assert.Equal("EVENT", zone.RentalMode);
        Assert.Equal(new DateOnly(2026, 12, 20), zone.EventStartDate);
        Assert.Equal(new DateOnly(2026, 12, 28), zone.EventEndDate);
        Assert.Equal("DAY", zone.PriceDisplayUnit);
        Assert.Equal(50_000m, zone.PricePerDay);
    }

    [Fact]
    public async Task Impact_preview_counts_pending_applications_and_open_renewals_at_the_new_price()
    {
        using var f = await Fixture.Create();
        var preview = await f.Service().PreviewZoneImpactAsync(f.Actor, 1,
            new ZoneImpactPreviewRequest(40_000, new TimeOnly(5, 0), new TimeOnly(22, 0), []), default);

        Assert.True(preview.AmountChanged);
        Assert.False(preview.HoursChanged);
        var app = Assert.Single(preview.PendingApplications);
        Assert.Equal(30 * 30_000m, app.CurrentTotal);
        Assert.Equal(30 * 40_000m, app.NewTotal);
        var renewal = Assert.Single(preview.OpenRenewals);
        Assert.Equal(60 * 40_000m, renewal.NewTotal);
        Assert.Equal(30 * 10_000m + 60 * 10_000m, preview.TotalDelta);
        Assert.Equal(1, preview.VendorsToNotify);
    }

    [Fact]
    public async Task Impact_preview_includes_fee_components_in_both_totals_and_flags_a_fee_only_change()
    {
        using var f = await Fixture.Create();
        var zone = await f.Service().GetZoneAsync(f.Actor, 1, default);
        // Give zone 1 a fee component first, same price/hours as before.
        var withFee = await f.Service().UpdateZoneAsync(f.Actor, 1, Edit(zone, price: 30_000) with
        {
            FeeComponents = [new ZoneFeeComponentInput("Phí vệ sinh", "PER_DAY", 3_000)],
        }, default);

        // Same price/hours as currently stored; only the fee amount changes 3,000 -> 5,000.
        var preview = await f.Service().PreviewZoneImpactAsync(f.Actor, 1, new ZoneImpactPreviewRequest(
            (long)withFee.PricePerDay, withFee.AvailableFrom, withFee.AvailableTo,
            [new ZoneFeeComponentInput("Phí vệ sinh", "PER_DAY", 5_000)]), default);

        Assert.True(preview.AmountChanged);
        Assert.False(preview.HoursChanged);
        var app = Assert.Single(preview.PendingApplications);
        // 30 days at 30,000 + 3,000 (current fee) vs 30,000 + 5,000 (new fee).
        Assert.Equal(30 * 33_000m, app.CurrentTotal);
        Assert.Equal(30 * 35_000m, app.NewTotal);
    }

    [Fact]
    public async Task Impact_preview_is_unchanged_when_price_hours_and_fees_are_all_the_same()
    {
        using var f = await Fixture.Create();
        var zone = await f.Service().GetZoneAsync(f.Actor, 1, default);
        var preview = await f.Service().PreviewZoneImpactAsync(f.Actor, 1,
            new ZoneImpactPreviewRequest((long)zone.PricePerDay, zone.AvailableFrom, zone.AvailableTo, []), default);

        Assert.False(preview.AmountChanged);
        Assert.False(preview.HoursChanged);
    }

    [Fact]
    public async Task Changing_price_and_hours_notifies_affected_vendors_and_keeps_before_after_in_the_audit()
    {
        using var f = await Fixture.Create();
        var zone = await f.Service().GetZoneAsync(f.Actor, 1, default);
        var updated = await f.Service().UpdateZoneAsync(f.Actor, 1,
            Edit(zone, price: 40_000) with { AvailableFrom = new TimeOnly(18, 0), AvailableTo = new TimeOnly(2, 0) }, default);

        Assert.True(updated.IsOvernight);
        using var db = f.NewDb();
        var types = await db.Notifications.Where(n => n.user_id == 10).Select(n => n.notification_type).ToListAsync();
        Assert.Contains("ZONE_PRICE_CHANGED", types);
        Assert.Contains("ZONE_HOURS_CHANGED", types);

        var audit = await db.AuditLogs.SingleAsync(a => a.action == "ZONE_UPDATED");
        using var details = JsonDocument.Parse(audit.details!);
        Assert.Equal(30_000, details.RootElement.GetProperty("before").GetProperty("pricePerDay").GetDecimal());
        Assert.Equal(40_000, details.RootElement.GetProperty("after").GetProperty("pricePerDay").GetDecimal());
        Assert.Equal("Điều chỉnh theo quyết định mới", details.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_fee_only_change_still_notifies_vendors_even_though_price_is_unchanged()
    {
        using var f = await Fixture.Create();
        var zone = await f.Service().GetZoneAsync(f.Actor, 1, default);
        await f.Service().UpdateZoneAsync(f.Actor, 1, Edit(zone, price: 30_000) with
        {
            FeeComponents = [new ZoneFeeComponentInput("Phí vệ sinh", "PER_DAY", 8_000)],
        }, default);

        using var db = f.NewDb();
        var types = await db.Notifications.Where(n => n.user_id == 10).Select(n => n.notification_type).ToListAsync();
        Assert.Contains("ZONE_FEES_CHANGED", types);
        Assert.DoesNotContain("ZONE_PRICE_CHANGED", types);
    }

    [Fact]
    public async Task A_zone_with_slots_cannot_be_deleted()
    {
        using var f = await Fixture.Create();
        var zone = await f.Service().GetZoneAsync(f.Actor, 1, default);
        var ex = await Assert.ThrowsAsync<WardException>(() => f.Service().DeleteZoneAsync(f.Actor, 1, zone.VersionToken, default));
        Assert.Equal("zone_has_slots", ex.Code);
    }

    [Fact]
    public async Task An_empty_zone_can_be_deleted()
    {
        using var f = await Fixture.Create();
        var created = await f.Service().CreateZoneAsync(f.Actor, ZoneRequest("Khu trống", "HC1-KT", 20_000), default);
        await f.Service().DeleteZoneAsync(f.Actor, created.ZoneId, created.VersionToken, default);
        Assert.DoesNotContain(await f.Service().ListZonesAsync(f.Actor, default), z => z.ZoneId == created.ZoneId);
    }

    [Fact]
    public async Task Another_wards_zone_is_not_found()
    {
        using var f = await Fixture.Create();
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service().GetZoneAsync(f.OtherActor, 1, default));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service().ListZoneHistoryAsync(f.OtherActor, 1, default));
    }
    #endregion

    #region WARD-01
    [Fact]
    public async Task A_new_slot_gets_the_next_code_in_its_zone_and_reports_an_unverified_boundary()
    {
        using var f = await Fixture.Create();
        var result = await f.Service().CreateSlotAsync(f.Actor, Slot(16.050000m, 108.220000m), default);

        Assert.Equal("HC1-NVL-001", result.Slot.SlotCode);
        Assert.False(result.Check.BoundaryVerified);
        Assert.True(result.Slot.CanHardDelete);
        Assert.Equal(1, await f.AuditCount("SLOT_CREATED"));
    }

    [Fact]
    public async Task A_slot_on_a_no_business_feature_is_blocked()
    {
        using var f = await Fixture.Create();
        var ex = await Assert.ThrowsAsync<WardException>(() =>
            f.Service().CreateSlotAsync(f.Actor, Slot(16.062000m, 108.220000m) with { AcknowledgeWarnings = true, WarningReason = "x" }, default));
        Assert.Equal("placement_blocked", ex.Code);
    }

    [Fact]
    public async Task Overlap_warnings_need_an_acknowledgement_with_a_reason()
    {
        using var f = await Fixture.Create();
        var service = f.Service();
        var near = Slot(16.060005m, 108.220000m);

        var ex = await Assert.ThrowsAsync<WardException>(() => service.CreateSlotAsync(f.Actor, near, default));
        Assert.Equal("placement_warnings", ex.Code);

        var ok = await service.CreateSlotAsync(f.Actor, near with { AcknowledgeWarnings = true, WarningReason = "Hai ô ghép cho cùng hộ" }, default);
        Assert.Contains(ok.Check.Issues, i => i.Code == "slot_overlap");

        using var db = f.NewDb();
        var audit = await db.AuditLogs.SingleAsync(a => a.action == "SLOT_CREATED");
        Assert.Contains("slot_overlap", audit.details);
        Assert.Contains("Hai ô ghép cho cùng hộ", audit.details);
    }

    [Fact]
    public async Task A_slot_outside_a_configured_boundary_is_blocked()
    {
        using var f = await Fixture.Create(geolocation: new FakeGeolocation(inside: false));
        var ex = await Assert.ThrowsAsync<WardException>(() => f.Service().CreateSlotAsync(f.Actor, Slot(16.050000m, 108.220000m), default));
        Assert.Equal("placement_blocked", ex.Code);
    }

    [Fact]
    public async Task A_broken_boundary_configuration_is_not_silently_ignored()
    {
        using var f = await Fixture.Create(geolocation: new FakeGeolocation(errorCode: "boundary_invalid"));
        var ex = await Assert.ThrowsAsync<WardException>(() => f.Service().CreateSlotAsync(f.Actor, Slot(16.050000m, 108.220000m), default));
        Assert.Equal(503, ex.StatusCode);
    }

    [Fact]
    public async Task Clearance_warnings_only_apply_when_enabled()
    {
        using var off = await Fixture.Create();
        var near = new SlotPlacementInput(1, 16.061970m, 108.220000m, 2, 2);
        Assert.DoesNotContain((await off.Service().CheckPlacementAsync(off.Actor, near, null, default)).Issues,
            i => i.Code == "feature_clearance");

        using var on = await Fixture.Create(clearanceEnabled: true);
        Assert.Contains((await on.Service().CheckPlacementAsync(on.Actor, near, null, default)).Issues, i => i.Code == "feature_clearance");
    }

    [Fact]
    public async Task Slots_with_history_cannot_be_hard_deleted_but_clean_ones_can()
    {
        using var f = await Fixture.Create();
        var grid = await f.Service().GetSlotGridAsync(f.Actor, null, default);
        var withApplication = grid.Slots.Single(s => s.SlotId == 101);
        Assert.False(withApplication.CanHardDelete);

        var clean = grid.Slots.Single(s => s.SlotId == 100);
        await f.Service().DeleteSlotAsync(f.Actor, 100, clean.VersionToken, default);
        Assert.Equal(1, await f.AuditCount("SLOT_DELETED"));
    }

    [Fact]
    public async Task A_slot_with_an_open_application_cannot_be_suspended_here()
    {
        using var f = await Fixture.Create();
        var slot = (await f.Service().GetSlotGridAsync(f.Actor, null, default)).Slots.Single(s => s.SlotId == 101);
        await f.SetSlotStatus(101, "AVAILABLE");
        slot = (await f.Service().GetSlotGridAsync(f.Actor, null, default)).Slots.Single(s => s.SlotId == 101);

        var ex = await Assert.ThrowsAsync<WardException>(() => f.Service().SetSlotStatusAsync(f.Actor, 101,
            new SetSlotStatusRequest("SUSPENDED", "Thi công", slot.VersionToken), default));
        Assert.Equal("slot_has_open_application", ex.Code);
    }

    [Fact]
    public async Task A_free_slot_can_be_suspended_and_reopened_with_a_reason()
    {
        using var f = await Fixture.Create();
        var slot = (await f.Service().GetSlotGridAsync(f.Actor, null, default)).Slots.Single(s => s.SlotId == 100);
        var suspended = await f.Service().SetSlotStatusAsync(f.Actor, 100, new SetSlotStatusRequest("SUSPENDED", "Thi công vỉa hè", slot.VersionToken), default);
        Assert.Equal("SUSPENDED", suspended.Status);

        var reopened = await f.Service().SetSlotStatusAsync(f.Actor, 100, new SetSlotStatusRequest("AVAILABLE", "Thi công xong", suspended.VersionToken), default);
        Assert.Equal("AVAILABLE", reopened.Status);
        Assert.Equal(2, await f.AuditCount("SLOT_STATUS_CHANGED"));
    }

    [Fact]
    public async Task Slot_history_lists_only_that_slots_changes_newest_first_with_reasons()
    {
        using var f = await Fixture.Create();
        var service = f.Service();
        var slot = (await service.GetSlotGridAsync(f.Actor, null, default)).Slots.Single(s => s.SlotId == 100);
        var suspended = await service.SetSlotStatusAsync(f.Actor, 100, new SetSlotStatusRequest("SUSPENDED", "Thi công vỉa hè", slot.VersionToken), default);
        await service.SetSlotStatusAsync(f.Actor, 100, new SetSlotStatusRequest("AVAILABLE", "Thi công xong", suspended.VersionToken), default);

        var history = await service.ListSlotHistoryAsync(f.Actor, 100, default);

        Assert.Equal(2, history.Count);
        Assert.All(history, h => Assert.Equal("SLOT_STATUS_CHANGED", h.Action));
        Assert.Contains("Thi công xong", history[0].Details);
        Assert.Contains("Thi công vỉa hè", history[1].Details);
        Assert.All(history, h => Assert.False(string.IsNullOrWhiteSpace(h.ActorName)));
        Assert.Empty(await service.ListSlotHistoryAsync(f.Actor, 101, default));
    }

    [Fact]
    public async Task Another_wards_slot_history_is_not_found()
    {
        using var f = await Fixture.Create();
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service().ListSlotHistoryAsync(f.OtherActor, 100, default));
    }

    [Fact]
    public async Task Moving_a_slot_that_is_not_free_is_refused()
    {
        using var f = await Fixture.Create();
        var slot = (await f.Service().GetSlotGridAsync(f.Actor, null, default)).Slots.Single(s => s.SlotId == 101);
        var ex = await Assert.ThrowsAsync<WardException>(() => f.Service().UpdateSlotAsync(f.Actor, 101, Move(slot, 16.070000m), default));
        Assert.Equal("slot_in_use", ex.Code);
    }

    [Fact]
    public async Task Vendor_proposed_slots_are_read_only_here()
    {
        using var f = await Fixture.Create();
        var slot = (await f.Service().GetSlotGridAsync(f.Actor, null, default)).Slots.Single(s => s.SlotId == 103);
        var ex = await Assert.ThrowsAsync<WardException>(() => f.Service().UpdateSlotAsync(f.Actor, 103, Move(slot, 16.070000m), default));
        Assert.Equal("vendor_proposed_readonly", ex.Code);
    }

    [Fact]
    public async Task A_stale_slot_token_is_a_conflict()
    {
        using var f = await Fixture.Create();
        var slot = (await f.Service().GetSlotGridAsync(f.Actor, null, default)).Slots.Single(s => s.SlotId == 100);
        await f.Service().UpdateSlotAsync(f.Actor, 100, Move(slot, 16.050000m), default);
        await Assert.ThrowsAsync<ConflictException>(() => f.Service().UpdateSlotAsync(f.Actor, 100, Move(slot, 16.051000m), default));
    }

    [Fact]
    public async Task Batch_preview_spaces_slots_along_the_segment_without_writing()
    {
        using var f = await Fixture.Create();
        // ~111 m north-south segment; 2 m slots with a 1 m gap -> 37 slots.
        var preview = await f.Service().PreviewBatchAsync(f.Actor,
            new BatchPreviewRequest(1, 16.030000m, 108.230000m, 16.031000m, 108.230000m, 1.5m, 2m, 1m), default);

        Assert.Equal(37, preview.Candidates.Count);
        Assert.Equal("HC1-NVL-001", preview.Candidates[0].ProposedCode);
        Assert.Equal("HC1-NVL-037", preview.Candidates[^1].ProposedCode);
        using var db = f.NewDb();
        Assert.Equal(4, await db.SidewalkSlots.CountAsync());
    }

    [Fact]
    public async Task A_batch_containing_a_blocked_position_creates_nothing()
    {
        using var f = await Fixture.Create();
        var ex = await Assert.ThrowsAsync<WardException>(() => f.Service().CreateBatchAsync(f.Actor, new BatchCreateRequest(1,
            [new(16.030000m, 108.230000m), new(16.062000m, 108.220000m)], 1.5m, 2m, false, false, false, null, true, "x"), default));
        Assert.Equal("placement_blocked", ex.Code);

        using var db = f.NewDb();
        Assert.Equal(4, await db.SidewalkSlots.CountAsync());
    }

    [Fact]
    public async Task A_clean_batch_is_created_in_one_transaction_with_one_audit_row()
    {
        using var f = await Fixture.Create();
        var created = await f.Service().CreateBatchAsync(f.Actor, new BatchCreateRequest(1,
            [new(16.030000m, 108.230000m), new(16.030030m, 108.230000m)], 1.5m, 2m, true, false, true, "FOOD_BEVERAGE", false, null), default);

        Assert.Equal(new[] { "HC1-NVL-001", "HC1-NVL-002" }, created.Select(s => s.SlotCode));
        Assert.Equal(1, await f.AuditCount("SLOT_BATCH_CREATED"));
    }

    [Fact]
    public async Task Adding_a_street_feature_reports_slots_it_now_touches()
    {
        using var f = await Fixture.Create();
        var result = await f.Service().CreateFeatureAsync(f.Actor,
            new UpsertStreetFeatureRequest(1, "HYDRANT", "Trụ nước số 3", 16.060000m, 108.220000m, true, null, null), default);

        var affected = Assert.Single(result.AffectedSlots);
        Assert.Equal(100, affected.SlotId);
        Assert.Equal(PlacementSeverities.Block, affected.Severity);
        Assert.Equal(1, await f.AuditCount("STREET_FEATURE_CREATED"));
    }

    [Fact]
    public async Task Street_features_follow_ward_scope_and_version_tokens()
    {
        using var f = await Fixture.Create();
        var feature = (await f.Service().GetSlotGridAsync(f.Actor, null, default)).Features.Single();
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service().DeleteFeatureAsync(f.OtherActor, feature.FeatureId, feature.VersionToken, default));
        await Assert.ThrowsAsync<ConflictException>(() => f.Service().DeleteFeatureAsync(f.Actor, feature.FeatureId, "stale", default));
        await f.Service().DeleteFeatureAsync(f.Actor, feature.FeatureId, feature.VersionToken, default);
        Assert.Empty((await f.Service().GetSlotGridAsync(f.Actor, null, default)).Features);
    }
    #endregion

    #region Helpers
    private static SetPenaltyRateRequest Rate(string type, long min, long max, DateOnly from, int? expected) =>
        type == "HYGIENE_LITTERING"
            ? new(type, "Nghị định 45/2022/NĐ-CP", "25", "2", "d", "vứt, thải, bỏ rác thải trên vỉa hè", min, max, from, expected)
            : new(type, "Nghị định 168/2024/NĐ-CP", "12", "5", null, "sử dụng trái phép vỉa hè để kinh doanh", min, max, from, expected);

    private static SanctionWardViolationRequest Sanction(int scheduleId) =>
        new(scheduleId, "QD-2026-010", null);

    private static UpsertZoneRequest ZoneRequest(string name, string code, long price) =>
        new(name, code, price, new TimeOnly(6, 0), new TimeOnly(21, 0), "QĐ 15/QĐ-UBND", new DateOnly(2026, 9, 1),
            "UBND phường Hải Châu", null, null, null,
            [new ZoneFeeComponentInput("Phí vệ sinh", "PER_DAY", 5_000), new ZoneFeeComponentInput("Phí kẻ vạch", "PER_TERM", 50_000)],
            null, null);

    private static UpsertZoneRequest Edit(WardZoneDto zone, long price) =>
        new(zone.ZoneName, zone.ZoneCode!, price, zone.AvailableFrom, zone.AvailableTo, null, null, null,
            zone.SegmentFrom, zone.SegmentTo, zone.ApplicationDeadline,
            zone.FeeComponents.Select(c => new ZoneFeeComponentInput(c.ComponentName, c.CalcBasis, (long)c.UnitAmount)).ToList(),
            "Điều chỉnh theo quyết định mới", zone.VersionToken);

    private static CreateSlotRequest Slot(decimal lat, decimal lng) =>
        new(1, null, lat, lng, 2m, 2m, false, false, false, null, false, null);

    private static UpdateSlotRequest Move(WardSlotDto slot, decimal lat) =>
        new(slot.ZoneId, slot.SlotCode, lat, slot.Longitude, slot.WidthMeters ?? 2, slot.LengthMeters ?? 2,
            slot.HasPower, slot.HasWater, slot.HasTrashBin, slot.BusinessCategory, slot.VersionToken, true, "Dời ô");

    private sealed class RecordWardViolationRequestFactory
    {
        public RecordWardViolationRequest For(long slotId, string type) =>
            new(ContractId: null, SlotId: slotId, VendorId: 10, ViolationType: type, Description: "Bày bán ngoài ô", EvidenceUrl: null);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeGeolocation(bool inside = true, string? errorCode = "boundary_unavailable") : IGeolocation
    {
        public FakeGeolocation(bool inside) : this(inside, null) { }

        public GeofenceResult Verify(int wardId, GeoPoint point) =>
            errorCode is null
                ? new GeofenceResult(inside, wardId, "test")
                : throw new WardException(503, errorCode, "boundary");
    }

    private sealed class Fixture : IDisposable
    {
        private SqliteConnection Connection { get; } = new("Data Source=:memory:");
        private TimeProvider Clock { get; init; } = new FixedClock(DefaultNow);
        private IGeolocation Geolocation { get; init; } = new FakeGeolocation();
        private bool ClearanceEnabled { get; init; }

        public WardActor Actor { get; } = new(1, 1, "Cán bộ phường 1", "Chủ tịch UBND Phường");
        public WardActor OtherActor { get; } = new(2, 2, "Cán bộ phường 2", "Chủ tịch UBND Phường");

        public static async Task<Fixture> Create(DateTimeOffset? now = null, IGeolocation? geolocation = null, bool clearanceEnabled = false)
        {
            var f = new Fixture
            {
                Clock = new FixedClock(now ?? DefaultNow),
                Geolocation = geolocation ?? new FakeGeolocation(),
                ClearanceEnabled = clearanceEnabled,
            };
            await f.Connection.OpenAsync();
            using var db = f.NewDb();
            await db.Database.EnsureCreatedAsync();

            db.Roles.AddRange(
                new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" },
                new Role { role_code = "VENDOR", role_name = "Vendor" });
            db.AdministrativeUnits.AddRange(
                new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Phường 1" },
                new AdministrativeUnit { unit_id = 2, unit_type = "WARD", unit_name = "Phường 2" });
            db.UserAccounts.AddRange(
                new UserAccount { user_id = 1, phone_number = "0900000001", password_hash = "x", full_name = "Cán bộ 1", role_code = "WARD_AUTHORITY", ward_unit_id = 1, account_status = "ACTIVE" },
                new UserAccount { user_id = 2, phone_number = "0900000002", password_hash = "x", full_name = "Cán bộ 2", role_code = "WARD_AUTHORITY", ward_unit_id = 2, account_status = "ACTIVE" },
                new UserAccount { user_id = 10, phone_number = "0900000010", password_hash = "x", full_name = "Chủ hộ A", role_code = "VENDOR", account_status = "ACTIVE" });
            db.Vendors.Add(new Vendor { vendor_id = 10, user_id = 10 });
            db.BusinessRegistrations.Add(new BusinessRegistration { registration_id = 1, vendor_id = 10, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Hộ A", registration_status = "APPROVED" });
            db.PricingZones.AddRange(
                new PricingZone { zone_id = 1, ward_unit_id = 1, zone_name = "Đường Nguyễn Văn Linh", zone_code = "HC1-NVL", price_per_day = 30_000, available_from = new TimeOnly(5, 0), available_to = new TimeOnly(22, 0), regulation_ref = "QĐ 1247/QĐ-UBND", created_by = 1 },
                new PricingZone { zone_id = 2, ward_unit_id = 2, zone_name = "Khu phường 2", zone_code = "P2-A", price_per_day = 20_000, regulation_ref = "QĐ 1/QĐ-UBND", created_by = 2 });
            db.SidewalkSlots.AddRange(
                new SidewalkSlot { slot_id = 100, zone_id = 1, slot_code = "OLD-100", source = "WARD_DEFINED", slot_status = "AVAILABLE", latitude = 16.060000m, longitude = 108.220000m, width_meters = 2, length_meters = 2 },
                new SidewalkSlot { slot_id = 101, zone_id = 1, slot_code = "OLD-101", source = "WARD_DEFINED", slot_status = "PENDING_APPLICATION", latitude = 16.061000m, longitude = 108.220000m, width_meters = 2, length_meters = 2 },
                new SidewalkSlot { slot_id = 102, zone_id = 1, slot_code = "OLD-102", source = "WARD_DEFINED", slot_status = "ACTIVE", latitude = 16.058000m, longitude = 108.220000m, width_meters = 2, length_meters = 2 },
                new SidewalkSlot { slot_id = 103, zone_id = 1, slot_code = "OLD-103", source = "VENDOR_PROPOSED", slot_status = "AVAILABLE", proposed_by_registration_id = 1, proposal_review_status = "APPROVED", proposal_photo_url = "p.jpg", latitude = 16.057000m, longitude = 108.220000m, width_meters = 2, length_meters = 2 });
            db.StreetFeatures.Add(new StreetFeature { feature_id = 1, zone_id = 1, feature_type = "TRANSFORMER", label = "Trạm biến áp", latitude = 16.062000m, longitude = 108.220000m, blocks_business = true });
            db.RentalApplications.AddRange(
                new RentalApplication { application_id = 1, registration_id = 1, slot_id = 101, application_method = "MANUAL_SELECTED", application_status = "PENDING", requested_term_days = 30 },
                new RentalApplication { application_id = 2, registration_id = 1, slot_id = 102, application_method = "MANUAL_SELECTED", application_status = "APPROVED", requested_term_days = 30 });
            db.RentalContracts.Add(new RentalContract { contract_id = 500, application_id = 2, slot_id = 102, vendor_id = 10, start_date = new DateOnly(2026, 9, 1), end_date = new DateOnly(2026, 10, 1), contract_status = "ACTIVE" });
            db.RenewalRequests.Add(new RenewalRequest { renewal_id = 1, contract_id = 500, requested_term_days = 60, renewal_status = "PENDING", created_at = DefaultNow.UtcDateTime });
            db.ViolationTypes.AddRange(
                new ViolationType { violation_type_code = "UNAUTHORIZED_BUSINESS_USE", description = "Sử dụng trái phép vỉa hè", is_active = true },
                new ViolationType { violation_type_code = "HYGIENE_LITTERING", description = "Vứt rác trên vỉa hè", is_active = true },
                new ViolationType { violation_type_code = "NO_PERMIT", description = "Không giấy phép", is_active = false });
            db.PenaltyFeeSchedules.Add(new PenaltyFeeSchedule
            {
                penalty_schedule_id = 1, ward_unit_id = 1, violation_type = "UNAUTHORIZED_BUSINESS_USE", penalty_amount = 2_500_000,
                legal_basis = "Nghị định 168/2024/NĐ-CP: sử dụng trái phép vỉa hè (khung 2.000.000 - 3.000.000đ)",
                effective_from = new DateOnly(2026, 1, 1), created_by = 1,
            });
            await db.SaveChangesAsync();
            return f;
        }

        public TestContext NewDb() => new(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(Connection).Options);

        public WardConfigurationService Service() => new(
            NewDb(),
            Geolocation,
            Options.Create(new SidewalkSettings { FeatureClearanceEnabled = ClearanceEnabled }),
            Clock);

        public WardComplianceService Compliance()
        {
            var db = NewDb();
            return new WardComplianceService(
                db,
                new PermitTokenService(Options.Create(new PermitSettings { SigningKey = "test-only-signing-key-0123456789" })),
                new NoOpAi(),
                new Persistence.Repositories.KycResultRepository(db, new Common.DateTimeProvider()),
                Clock,
                new Services.Documents.RegistrationDocumentGenerator());
        }

        public async Task<int> AuditCount(string action)
        {
            using var db = NewDb();
            return await db.AuditLogs.CountAsync(a => a.action == action);
        }

        public async Task<long> RecordViolation(string type)
        {
            await Compliance().RecordViolationAsync(Actor, new RecordWardViolationRequestFactory().For(100, type), default);
            using var db = NewDb();
            return await db.Violations.OrderByDescending(v => v.violation_id).Select(v => v.violation_id).FirstAsync();
        }

        public async Task AddRate(int id, string type, decimal amount, string? legalBasis)
        {
            using var db = NewDb();
            db.PenaltyFeeSchedules.Add(new PenaltyFeeSchedule
            {
                penalty_schedule_id = id, ward_unit_id = 1, violation_type = type, penalty_amount = amount,
                legal_basis = legalBasis, effective_from = new DateOnly(2026, 1, 1), created_by = 1,
            });
            await db.SaveChangesAsync();
        }

        public async Task AddPenaltyUsing(int scheduleId, string type)
        {
            using var db = NewDb();
            var violation = new Violation { slot_id = 100, violation_type = type, recorded_by = 1, source = "ON_SITE", recorded_at = DefaultNow.UtcDateTime };
            db.Violations.Add(violation);
            await db.SaveChangesAsync();
            db.Penalties.Add(new Penalty { violation_id = violation.violation_id, penalty_schedule_id = scheduleId, amount = 1, penalty_status = "UNPAID" });
            await db.SaveChangesAsync();
        }

        public async Task SetSlotStatus(long slotId, string status)
        {
            using var db = NewDb();
            var slot = await db.SidewalkSlots.SingleAsync(s => s.slot_id == slotId);
            slot.slot_status = status;
            await db.SaveChangesAsync();
        }

        public void Dispose() => Connection.Dispose();
    }

    private sealed class NoOpAi : IAiComplianceService
    {
        public Task<AiIdExtractionResult> ExtractIdDocumentAsync(IReadOnlyList<WardEvidenceDto> evidence, CancellationToken ct) =>
            Task.FromResult(new AiIdExtractionResult(null, null, null, 0, false));

        public Task<AiDocumentCheckResult> CompareDeclaredProfileAsync(string declaredName, string? extractedIdNumber, string declaredAddress, AiIdExtractionResult extraction, CancellationToken ct) =>
            Task.FromResult(new AiDocumentCheckResult(0, false, true, "[test]", Array.Empty<string>(), false));

        public Task<AiEncroachmentResult> AnalyzeInspectionPhotoAsync(string photoUrl, double? slotWidth, double? slotLength, CancellationToken ct) =>
            Task.FromResult(new AiEncroachmentResult(false, 0, "[test]", Array.Empty<string>(), false));

        public Task<AiLegalSuggestion> ClassifyAndDraftAsync(string? description, IReadOnlyList<PenaltyScheduleItemDto> availableSchedules, CancellationToken ct) =>
            Task.FromResult(new AiLegalSuggestion("UNKNOWN", null, null, null, "[test]", "[test]", false));

        public Task<string> AnswerVendorAssistantAsync(string question, string? context, CancellationToken ct) =>
            Task.FromResult("[test]");
    }

    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
                foreach (var property in entity.GetProperties())
                {
                    if (property.GetComputedColumnSql() is not null)
                    {
                        property.SetComputedColumnSql(null);
                        property.ValueGenerated = ValueGenerated.Never;
                    }
                    property.SetDefaultValueSql(null);
                }
        }
    }
    #endregion
}
