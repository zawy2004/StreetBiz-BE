using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Features.AiAssistance;
using StreetBiz.Application.Features.WardCompliance;
using StreetBiz.Application.Features.WardConfiguration;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// WardAiInsights orchestrates AIC-04 (proposed-slot feasibility), AIC-06 (geofence drift) and
/// AIC-07 (zone price suggestion). These tests cover ward scoping, the deterministic occupancy
/// slot-day union (including CANCELLED truncation), the drift window/revoked-permit filters, and
/// the cache-via-log behavior that stops a page reload from re-billing the AI provider.
/// </summary>
public sealed class WardAiInsightsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);

    #region AIC-07: zone price suggestion
    [Fact]
    public async Task Price_suggestion_unions_overlapping_contracts_and_truncates_a_cancelled_one_at_its_cancel_date()
    {
        using var f = await Fixture.Create();

        var result = await f.NewService().GetZonePriceSuggestionAsync(f.Actor, f.HighOccupancyZoneId, default);

        // slot A: ACTIVE for the full 90-day window = 90 slot-days.
        // slot B: ACTIVE Jul 5 - Sep 15 (73 days) + CANCELLED Sep 16 - Dec 31 but cut off at its
        // cancelled_at of Oct 1 (16 more days) = 89 slot-days. Total 179 / 180 available.
        Assert.Equal(179, result.OccupiedSlotDays);
        Assert.Equal(180, result.AvailableSlotDays);
        Assert.Equal(PriceDirections.Raise, result.Direction);
        Assert.False(result.IsAiGenerated);
        Assert.Null(result.AiLogId);
    }

    [Fact]
    public async Task Price_suggestion_for_a_zone_with_no_slots_is_insufficient_data_and_never_calls_the_ai_provider()
    {
        using var f = await Fixture.Create();
        var ai = new CountingAiComplianceService();

        var result = await f.NewService(ai: ai).GetZonePriceSuggestionAsync(f.Actor, f.EmptyZoneId, default);

        Assert.Equal(PriceDirections.InsufficientData, result.Direction);
        Assert.False(result.IsAiGenerated);
        Assert.Equal(0, ai.PriceAdvices);
    }

    [Fact]
    public async Task Price_suggestion_for_another_wards_zone_is_not_found()
    {
        using var f = await Fixture.Create();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            f.NewService().GetZonePriceSuggestionAsync(f.Actor, f.OtherWardZoneId, default));
    }

    [Fact]
    public async Task Price_suggestion_second_call_with_unchanged_data_reuses_the_logged_answer_without_calling_ai_again()
    {
        using var f = await Fixture.Create();
        var ai = new CountingAiComplianceService { PriceAdviceToReturn = new AiPriceAdvice(32000, "Nên tăng giá") };

        var first = await f.NewService(ai: ai).GetZonePriceSuggestionAsync(f.Actor, f.HighOccupancyZoneId, default);
        var second = await f.NewService(ai: ai).GetZonePriceSuggestionAsync(f.Actor, f.HighOccupancyZoneId, default);

        Assert.Equal(1, ai.PriceAdvices);
        Assert.True(first.IsAiGenerated);
        Assert.NotNull(first.AiLogId);
        Assert.Equal(first.AiLogId, second.AiLogId);
        Assert.Equal(first.SuggestedPricePerDay, second.SuggestedPricePerDay);
    }
    #endregion

    #region AIC-06: geofence drift
    [Fact]
    public async Task Geofence_drift_only_reports_this_wards_active_or_suspended_non_revoked_permits_within_the_window()
    {
        using var f = await Fixture.Create();

        var report = await f.NewService().GetGeofenceDriftAsync(f.Actor, default);

        var permitIds = report.Items.Select(i => i.PermitId).ToHashSet();
        Assert.Contains(f.ConsistentDriftPermitId, permitIds);
        Assert.Contains(f.WatchPermitId, permitIds);
        Assert.Contains(f.ScatteredDriftPermitId, permitIds);
        // Excluded: another ward's permit, a revoked permit, and a scan older than the 30-day window.
        Assert.DoesNotContain(f.OtherWardPermitId, permitIds);
        Assert.DoesNotContain(f.RevokedPermitId, permitIds);
        Assert.DoesNotContain(f.StaleScanOnlyPermitId, permitIds);
    }

    [Fact]
    public async Task Geofence_drift_detects_a_consistent_direction_pattern_and_a_single_off_site_scan_as_watch()
    {
        using var f = await Fixture.Create();

        var report = await f.NewService().GetGeofenceDriftAsync(f.Actor, default);

        var consistent = report.Items.Single(i => i.PermitId == f.ConsistentDriftPermitId);
        Assert.Equal(DriftLevels.Drift, consistent.Level);
        Assert.Equal(DriftPatterns.ConsistentDirection, consistent.Pattern);
        Assert.Equal(2, consistent.OffSiteCount);

        var watch = report.Items.Single(i => i.PermitId == f.WatchPermitId);
        Assert.Equal(DriftLevels.Watch, watch.Level);
        Assert.Equal(1, watch.OffSiteCount);

        var scattered = report.Items.Single(i => i.PermitId == f.ScatteredDriftPermitId);
        Assert.Equal(DriftLevels.Drift, scattered.Level);
        Assert.Equal(DriftPatterns.Scattered, scattered.Pattern);
    }

    [Fact]
    public async Task Geofence_drift_explanation_for_a_drift_permit_is_logged_and_not_re_requested_on_the_next_call()
    {
        using var f = await Fixture.Create();
        var ai = new CountingAiComplianceService
        {
            DriftExplanations = ids => ids.ToDictionary(id => id, _ => "Hộ có thể đã dời sạp hàng."),
        };

        var first = await f.NewService(ai: ai).GetGeofenceDriftAsync(f.Actor, default);
        var second = await f.NewService(ai: ai).GetGeofenceDriftAsync(f.Actor, default);

        Assert.Equal(1, ai.DriftExplanationCalls);
        var firstItem = first.Items.Single(i => i.PermitId == f.ConsistentDriftPermitId);
        var secondItem = second.Items.Single(i => i.PermitId == f.ConsistentDriftPermitId);
        Assert.True(firstItem.IsAiGenerated);
        Assert.NotNull(firstItem.AiLogId);
        Assert.Equal(firstItem.AiLogId, secondItem.AiLogId);
    }
    #endregion

    #region AIC-04: proposed-slot feasibility
    [Fact]
    public async Task Proposal_assessment_before_any_run_has_no_latest_result()
    {
        using var f = await Fixture.Create();

        var view = await f.NewService().GetProposalAssessmentAsync(f.Actor, f.PendingProposalSlotId, default);

        Assert.Null(view.Latest);
        Assert.False(view.IsStale);
    }

    [Fact]
    public async Task Proposal_assessment_cannot_be_run_once_the_proposal_is_no_longer_pending()
    {
        using var f = await Fixture.Create();

        var ex = await Assert.ThrowsAsync<WardException>(() =>
            f.NewService().RunProposalAssessmentAsync(f.Actor, f.DecidedProposalSlotId, default));

        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Proposal_assessment_for_another_wards_proposal_is_not_found()
    {
        using var f = await Fixture.Create();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            f.NewService().RunProposalAssessmentAsync(f.Actor, f.OtherWardProposalSlotId, default));
    }

    [Fact]
    public async Task Proposal_assessment_logs_an_ai_generated_result_and_reuses_it_until_the_input_changes()
    {
        using var f = await Fixture.Create();
        var ai = new CountingAiComplianceService
        {
            ProposalAssessmentToReturn = input => AiInsightRules.MergeProposal(
                input, modelWidthMeters: 4.0m, modelObstructionLevel: "LOW",
                modelConfidence: 80, modelReasons: ["Vỉa hè còn trống nhiều"],
                usedSatelliteImage: true, usedProposalPhoto: false),
        };

        var run1 = await f.NewService(ai: ai).RunProposalAssessmentAsync(f.Actor, f.PendingProposalSlotId, default);
        Assert.True(run1.IsAiGenerated);
        Assert.NotNull(run1.AiLogId);
        Assert.Equal(1, ai.ProposalAssessments);

        var view = await f.NewService(ai: ai).GetProposalAssessmentAsync(f.Actor, f.PendingProposalSlotId, default);
        Assert.NotNull(view.Latest);
        Assert.False(view.IsStale);
        Assert.Equal(run1.AiLogId, view.Latest!.AiLogId);

        // Same geometry, same rule-check result: a second run must reuse the log instead of
        // spending another model call.
        var run2 = await f.NewService(ai: ai).RunProposalAssessmentAsync(f.Actor, f.PendingProposalSlotId, default);
        Assert.Equal(1, ai.ProposalAssessments);
        Assert.Equal(run1.AiLogId, run2.AiLogId);

        // Changing the proposal's own geometry invalidates the cached answer.
        using (var db = f.NewDb())
        {
            var slot = await db.SidewalkSlots.SingleAsync(s => s.slot_id == f.PendingProposalSlotId);
            slot.width_meters = 3.5m;
            await db.SaveChangesAsync();
        }

        var staleView = await f.NewService(ai: ai).GetProposalAssessmentAsync(f.Actor, f.PendingProposalSlotId, default);
        Assert.True(staleView.IsStale);
    }

    [Fact]
    public async Task Proposal_assessment_does_not_log_a_non_ai_fallback_result()
    {
        using var f = await Fixture.Create();
        var ai = new CountingAiComplianceService(); // falls back (ProposalAssessmentToReturn unset)

        var result = await f.NewService(ai: ai).RunProposalAssessmentAsync(f.Actor, f.PendingProposalSlotId, default);

        Assert.False(result.IsAiGenerated);
        Assert.Null(result.AiLogId);
        using var db = f.NewDb();
        Assert.Equal(0, await db.AIAssistanceLogs.CountAsync());
    }
    #endregion

    #region AIC-02 retrofit: ward-scoped encroachment check
    [Fact]
    public async Task Encroachment_check_with_a_slot_from_another_ward_is_not_found()
    {
        using var f = await Fixture.Create();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            f.NewService().CheckEncroachmentAsync(f.Actor, "https://example.test/photo.jpg", null, null, f.OtherWardSlotId, default));
    }

    [Fact]
    public async Task Encroachment_check_with_a_slot_id_uses_the_slots_own_dimensions_and_logs_against_the_slot()
    {
        using var f = await Fixture.Create();
        double? capturedWidth = null, capturedLength = null;
        var ai = new CountingAiComplianceService
        {
            EncroachmentResultToReturn = (w, l) =>
            {
                capturedWidth = w;
                capturedLength = l;
                return new AiEncroachmentResult(false, 0, "[AI] Không phát hiện lấn chiếm", [], IsAiGenerated: true);
            },
        };

        var result = await f.NewService(ai: ai).CheckEncroachmentAsync(
            f.Actor, "https://example.test/photo.jpg", slotWidth: 99, slotLength: 99, f.HighOccupancySlotAId, default);

        Assert.NotNull(result.AiLogId);
        Assert.Equal(2.0, capturedWidth);
        Assert.Equal(2.0, capturedLength);

        using var db = f.NewDb();
        var log = await db.AIAssistanceLogs.SingleAsync();
        Assert.Equal(AiLogEntities.Slot, log.entity_type);
        Assert.Equal(f.HighOccupancySlotAId, log.entity_id);
    }

    [Fact]
    public async Task Encroachment_check_without_a_slot_id_logs_against_the_ward_itself()
    {
        using var f = await Fixture.Create();
        var ai = new CountingAiComplianceService
        {
            EncroachmentResultToReturn = (_, _) => new AiEncroachmentResult(true, 30, "[AI] Có lấn chiếm", [], IsAiGenerated: true),
        };

        await f.NewService(ai: ai).CheckEncroachmentAsync(f.Actor, "https://example.test/photo.jpg", 2, 2, slotId: null, default);

        using var db = f.NewDb();
        var log = await db.AIAssistanceLogs.SingleAsync();
        Assert.Equal(AiLogEntities.Ward, log.entity_type);
        Assert.Equal(f.Actor.WardId, log.entity_id);
    }
    #endregion

    private sealed class Fixture : IDisposable
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public WardActor Actor { get; } = new(1, 1, "Cán bộ phường");

        public int HighOccupancyZoneId { get; private set; }
        public int EmptyZoneId { get; private set; }
        public int OtherWardZoneId { get; private set; }
        public long HighOccupancySlotAId { get; private set; }
        public long OtherWardSlotId { get; private set; }

        public long ConsistentDriftPermitId { get; private set; }
        public long WatchPermitId { get; private set; }
        public long ScatteredDriftPermitId { get; private set; }
        public long OtherWardPermitId { get; private set; }
        public long RevokedPermitId { get; private set; }
        public long StaleScanOnlyPermitId { get; private set; }

        public long PendingProposalSlotId { get; private set; }
        public long DecidedProposalSlotId { get; private set; }
        public long OtherWardProposalSlotId { get; private set; }

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
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
                new UserAccount { user_id = 1, phone_number = "0900000001", password_hash = "x", full_name = "Cán bộ", role_code = "WARD_AUTHORITY", ward_unit_id = 1, account_status = "ACTIVE" },
                new UserAccount { user_id = 10, phone_number = "0900000010", password_hash = "x", full_name = "Chủ hộ A", role_code = "VENDOR", account_status = "ACTIVE" });
            db.Vendors.Add(new Vendor { vendor_id = 10, user_id = 10 });
            db.BusinessRegistrations.Add(
                new BusinessRegistration { registration_id = 1, vendor_id = 10, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Hộ A", registration_status = "APPROVED" });

            f.HighOccupancyZoneId = 1;
            f.EmptyZoneId = 2;
            f.OtherWardZoneId = 3;
            const int driftZoneId = 4; // keeps AIC-06's own slots/contracts out of the AIC-07 occupancy math above.
            db.PricingZones.AddRange(
                new PricingZone { zone_id = f.HighOccupancyZoneId, ward_unit_id = 1, zone_code = "T1", zone_name = "Tuyến 1", price_per_day = 30000, created_by = 1 },
                new PricingZone { zone_id = f.EmptyZoneId, ward_unit_id = 1, zone_code = "T2", zone_name = "Tuyến 2", price_per_day = 25000, created_by = 1 },
                new PricingZone { zone_id = f.OtherWardZoneId, ward_unit_id = 2, zone_code = "T3", zone_name = "Tuyến phường khác", price_per_day = 20000, created_by = 1 },
                new PricingZone { zone_id = driftZoneId, ward_unit_id = 1, zone_code = "T4", zone_name = "Tuyến tuần tra", price_per_day = 30000, created_by = 1 });

            // --- AIC-07 slots: two ward-defined slots in the high-occupancy zone ---
            f.HighOccupancySlotAId = 1;
            const long slotB = 2;
            db.SidewalkSlots.AddRange(
                new SidewalkSlot { slot_id = f.HighOccupancySlotAId, zone_id = f.HighOccupancyZoneId, slot_code = "A-01", source = "WARD_DEFINED", slot_status = "ACTIVE", latitude = 10m, longitude = 106m, width_meters = 2m, length_meters = 2m },
                new SidewalkSlot { slot_id = slotB, zone_id = f.HighOccupancyZoneId, slot_code = "A-02", source = "WARD_DEFINED", slot_status = "ACTIVE", latitude = 10.001m, longitude = 106.001m, width_meters = 2m, length_meters = 2m });

            db.RentalApplications.AddRange(
                new RentalApplication { application_id = 1, registration_id = 1, slot_id = f.HighOccupancySlotAId, application_method = "MANUAL_SELECTED", application_status = "REJECTED", requested_term_days = 30, created_at = Now.AddDays(-10).UtcDateTime },
                new RentalApplication { application_id = 2, registration_id = 1, slot_id = f.HighOccupancySlotAId, application_method = "MANUAL_SELECTED", application_status = "PENDING", requested_term_days = 30, created_at = Now.AddDays(-5).UtcDateTime },
                // Each contract below needs its own 1:1 application (RentalApplication.RentalContract is singular).
                new RentalApplication { application_id = 3, registration_id = 1, slot_id = f.HighOccupancySlotAId, application_method = "MANUAL_SELECTED", application_status = "APPROVED", requested_term_days = 180, created_at = Now.AddDays(-90).UtcDateTime },
                new RentalApplication { application_id = 4, registration_id = 1, slot_id = slotB, application_method = "MANUAL_SELECTED", application_status = "APPROVED", requested_term_days = 72, created_at = Now.AddDays(-90).UtcDateTime },
                new RentalApplication { application_id = 5, registration_id = 1, slot_id = slotB, application_method = "MANUAL_SELECTED", application_status = "APPROVED", requested_term_days = 106, created_at = Now.AddDays(-20).UtcDateTime });

            db.RentalContracts.AddRange(
                // slot A: ACTIVE across the entire 90-day window.
                new RentalContract { contract_id = 1, application_id = 3, slot_id = f.HighOccupancySlotAId, vendor_id = 10, start_date = new DateOnly(2026, 7, 5), end_date = new DateOnly(2027, 1, 1), contract_status = "ACTIVE", created_at = Now.AddDays(-90).UtcDateTime },
                // slot B: ACTIVE Jul 5 - Sep 15 (73 days)...
                new RentalContract { contract_id = 2, application_id = 4, slot_id = slotB, vendor_id = 10, start_date = new DateOnly(2026, 7, 5), end_date = new DateOnly(2026, 9, 15), contract_status = "ACTIVE", created_at = Now.AddDays(-90).UtcDateTime },
                // ...then CANCELLED Sep 16 - Dec 31, but cut off at cancelled_at = Oct 1 (16 more days).
                new RentalContract { contract_id = 3, application_id = 5, slot_id = slotB, vendor_id = 10, start_date = new DateOnly(2026, 9, 16), end_date = new DateOnly(2026, 12, 31), contract_status = "CANCELLED", cancelled_at = new DateTime(2026, 10, 1), created_at = Now.AddDays(-20).UtcDateTime });

            f.OtherWardSlotId = 90;
            db.SidewalkSlots.Add(
                new SidewalkSlot { slot_id = f.OtherWardSlotId, zone_id = f.OtherWardZoneId, slot_code = "C-01", source = "WARD_DEFINED", slot_status = "ACTIVE", latitude = 20m, longitude = 108m, width_meters = 3m, length_meters = 3m });

            // --- AIC-06 permits: one contract+permit per scenario, each on its own slot ---
            f.ConsistentDriftPermitId = AddPermitWithScans(db, slotId: 10, zoneId: driftZoneId, contractId: 10, permitId: 10,
                anchorLat: 10m, anchorLon: 106m, permitStatus: "ACTIVE", contractStatus: "ACTIVE",
                scans:
                [
                    (10.00085337, 106.0, Now.AddDays(-3)), // ~95m due north
                    (10.00085337, 106.0, Now.AddDays(-1)), // same spot again -> consistent direction
                ]);

            f.WatchPermitId = AddPermitWithScans(db, slotId: 11, zoneId: driftZoneId, contractId: 11, permitId: 11,
                anchorLat: 10.1m, anchorLon: 106.1m, permitStatus: "ACTIVE", contractStatus: "ACTIVE",
                scans: [(10.1003593, 106.1, Now.AddDays(-2))]); // ~40m off, single scan -> WATCH

            f.ScatteredDriftPermitId = AddPermitWithScans(db, slotId: 12, zoneId: driftZoneId, contractId: 12, permitId: 12,
                anchorLat: 10.2m, anchorLon: 106.1m, permitStatus: "ACTIVE", contractStatus: "SUSPENDED",
                scans:
                [
                    (10.2007186, 106.1, Now.AddDays(-4)), // ~80m north
                    (10.1992814, 106.1, Now.AddDays(-2)), // ~80m south -> offsets cancel out, scattered
                ]);

            f.OtherWardPermitId = AddPermitWithScans(db, slotId: 13, zoneId: f.OtherWardZoneId, contractId: 13, permitId: 13,
                anchorLat: 20m, anchorLon: 108m, permitStatus: "ACTIVE", contractStatus: "ACTIVE",
                scans: [(20.001, 108.001, Now.AddDays(-2))]);

            f.RevokedPermitId = AddPermitWithScans(db, slotId: 14, zoneId: driftZoneId, contractId: 14, permitId: 14,
                anchorLat: 10.3m, anchorLon: 106.1m, permitStatus: "REVOKED", contractStatus: "ACTIVE",
                scans: [(10.301, 106.1, Now.AddDays(-2))]);

            f.StaleScanOnlyPermitId = AddPermitWithScans(db, slotId: 15, zoneId: driftZoneId, contractId: 15, permitId: 15,
                anchorLat: 10.4m, anchorLon: 106.1m, permitStatus: "ACTIVE", contractStatus: "ACTIVE",
                scans: [(10.401, 106.1, Now.AddDays(-40))]); // outside the 30-day window

            // --- AIC-04 proposed slots ---
            f.PendingProposalSlotId = 50;
            f.DecidedProposalSlotId = 51;
            f.OtherWardProposalSlotId = 52;
            db.SidewalkSlots.AddRange(
                // zone = driftZoneId, not the price-test zone: an APPROVED proposal slot would
                // otherwise join the price test's own slot grid (source WARD_DEFINED OR proposal
                // APPROVED) and inflate its denominator.
                new SidewalkSlot { slot_id = f.PendingProposalSlotId, zone_id = driftZoneId, slot_code = "P-01", source = "VENDOR_PROPOSED", slot_status = "PENDING_APPLICATION", proposal_review_status = "PENDING", proposed_by_registration_id = 1, latitude = 10.5m, longitude = 106.1m, width_meters = 2m, length_meters = 2m },
                new SidewalkSlot { slot_id = f.DecidedProposalSlotId, zone_id = driftZoneId, slot_code = "P-02", source = "VENDOR_PROPOSED", slot_status = "PENDING_APPLICATION", proposal_review_status = "APPROVED", proposed_by_registration_id = 1, latitude = 10.6m, longitude = 106.1m, width_meters = 2m, length_meters = 2m },
                new SidewalkSlot { slot_id = f.OtherWardProposalSlotId, zone_id = f.OtherWardZoneId, slot_code = "P-03", source = "VENDOR_PROPOSED", slot_status = "PENDING_APPLICATION", proposal_review_status = "PENDING", proposed_by_registration_id = 1, latitude = 20.1m, longitude = 108.1m, width_meters = 2m, length_meters = 2m });

            await db.SaveChangesAsync();
            return f;

            static long AddPermitWithScans(
                TestContext db, long slotId, int zoneId, long contractId, long permitId,
                decimal anchorLat, decimal anchorLon, string permitStatus, string contractStatus,
                (double Lat, double Lon, DateTimeOffset ScannedAt)[] scans)
            {
                db.SidewalkSlots.Add(new SidewalkSlot
                {
                    slot_id = slotId, zone_id = zoneId, slot_code = $"D-{slotId}", source = "WARD_DEFINED",
                    slot_status = "ACTIVE", latitude = anchorLat, longitude = anchorLon, width_meters = 2m, length_meters = 2m,
                });
                // Each contract needs its own 1:1 application (RentalApplication.RentalContract is singular);
                // application_id mirrors contract_id here since these rows exist only to satisfy the FK chain.
                db.RentalApplications.Add(new RentalApplication
                {
                    application_id = contractId, registration_id = 1, slot_id = slotId,
                    application_method = "MANUAL_SELECTED", application_status = "APPROVED",
                    requested_term_days = 180, created_at = Now.AddDays(-90).UtcDateTime,
                });
                db.RentalContracts.Add(new RentalContract
                {
                    contract_id = contractId, application_id = contractId, slot_id = slotId, vendor_id = 10,
                    start_date = new DateOnly(2026, 7, 1), end_date = new DateOnly(2027, 1, 1),
                    contract_status = contractStatus, created_at = Now.AddDays(-90).UtcDateTime,
                });
                db.DigitalPermits.Add(new DigitalPermit
                {
                    permit_id = permitId, contract_id = contractId, qr_payload = $"QR-{permitId}",
                    permit_status = permitStatus, issued_at = Now.AddDays(-90).UtcDateTime,
                });
                var scanId = permitId * 100;
                foreach (var (lat, lon, scannedAt) in scans)
                {
                    db.PermitScanLogs.Add(new PermitScanLog
                    {
                        scan_id = scanId++, permit_id = permitId, qr_payload = $"QR-{permitId}",
                        scan_context = "WARD_INSPECTION", scan_result = "VALID",
                        latitude = (decimal)lat, longitude = (decimal)lon, scanned_at = scannedAt.UtcDateTime,
                    });
                }

                return permitId;
            }
        }

        public TestContext NewDb() =>
            new(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(Connection).Options);

        public WardAiInsights NewService(IAiComplianceService? ai = null, IWardConfigurationService? wardConfig = null)
        {
            var db = NewDb();
            return new WardAiInsights(
                db,
                ai ?? new CountingAiComplianceService(),
                new AiAssistanceLogs(db, new FixedClock(Now)),
                wardConfig ?? new StubWardConfigurationService(),
                new FixedClock(Now));
        }

        public void Dispose() => Connection.Dispose();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Only CheckPlacementAsync is reachable from WardAiInsights (AIC-04's rule checks);
    /// every other member belongs to WARD-01/02/03 and is already covered by
    /// WardConfigurationServiceTests, so it is left unimplemented here on purpose.</summary>
    private sealed class StubWardConfigurationService : IWardConfigurationService
    {
        public Task<PlacementCheckDto> CheckPlacementAsync(WardActor actor, SlotPlacementInput input, long? ignoreSlotId, CancellationToken ct) =>
            Task.FromResult(new PlacementCheckDto(BoundaryVerified: true, Issues: []));

        public Task<IReadOnlyList<WardPenaltyTypeDto>> ListPenaltyOverviewAsync(WardActor actor, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<PenaltyRateDto>> ListPenaltyHistoryAsync(WardActor actor, string violationType, CancellationToken ct) => throw new NotImplementedException();
        public Task<WardPenaltyTypeDto> SetPenaltyRateAsync(WardActor actor, SetPenaltyRateRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<WardPenaltyTypeDto> CancelScheduledPenaltyRateAsync(WardActor actor, int scheduleId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<WardZoneDto>> ListZonesAsync(WardActor actor, CancellationToken ct) => throw new NotImplementedException();
        public Task<WardZoneDto> GetZoneAsync(WardActor actor, int zoneId, CancellationToken ct) => throw new NotImplementedException();
        public Task<WardZoneDto> CreateZoneAsync(WardActor actor, UpsertZoneRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<WardZoneDto> UpdateZoneAsync(WardActor actor, int zoneId, UpsertZoneRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteZoneAsync(WardActor actor, int zoneId, string versionToken, CancellationToken ct) => throw new NotImplementedException();
        public Task<ZoneImpactPreviewDto> PreviewZoneImpactAsync(WardActor actor, int zoneId, ZoneImpactPreviewRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ConfigHistoryEntryDto>> ListZoneHistoryAsync(WardActor actor, int zoneId, CancellationToken ct) => throw new NotImplementedException();
        public Task<WardSlotGridDto> GetSlotGridAsync(WardActor actor, int? zoneId, CancellationToken ct) => throw new NotImplementedException();
        public Task<SlotMutationResultDto> CreateSlotAsync(WardActor actor, CreateSlotRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<SlotMutationResultDto> UpdateSlotAsync(WardActor actor, long slotId, UpdateSlotRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<WardSlotDto> SetSlotStatusAsync(WardActor actor, long slotId, SetSlotStatusRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteSlotAsync(WardActor actor, long slotId, string versionToken, CancellationToken ct) => throw new NotImplementedException();
        public Task<BatchPreviewDto> PreviewBatchAsync(WardActor actor, BatchPreviewRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<WardSlotDto>> CreateBatchAsync(WardActor actor, BatchCreateRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<StreetFeatureMutationResultDto> CreateFeatureAsync(WardActor actor, UpsertStreetFeatureRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<StreetFeatureMutationResultDto> UpdateFeatureAsync(WardActor actor, int featureId, UpsertStreetFeatureRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteFeatureAsync(WardActor actor, int featureId, string versionToken, CancellationToken ct) => throw new NotImplementedException();
    }

    /// <summary>Counts and controls AIC-04/06/07 provider calls; the AIC-01/02/03 members are
    /// never exercised by these tests (encroachment aside) and just answer like NoOp.</summary>
    private sealed class CountingAiComplianceService : IAiComplianceService
    {
        public int ProposalAssessments { get; private set; }
        public int DriftExplanationCalls { get; private set; }
        public int PriceAdvices { get; private set; }

        public Func<AiProposalSiteInput, AiProposalAssessment>? ProposalAssessmentToReturn { get; init; }
        public Func<IEnumerable<long>, IReadOnlyDictionary<long, string>>? DriftExplanations { get; init; }
        public AiPriceAdvice? PriceAdviceToReturn { get; init; }
        public Func<double?, double?, AiEncroachmentResult>? EncroachmentResultToReturn { get; init; }

        public Task<AiProposalAssessment> AssessProposalSiteAsync(AiProposalSiteInput input, CancellationToken ct)
        {
            ProposalAssessments++;
            var result = ProposalAssessmentToReturn?.Invoke(input) ?? AiInsightRules.FallbackProposalAssessment(input);
            return Task.FromResult(result);
        }

        public Task<IReadOnlyDictionary<long, string>?> ExplainGeofenceDriftAsync(IReadOnlyList<GeofenceDriftFacts> items, CancellationToken ct)
        {
            DriftExplanationCalls++;
            IReadOnlyDictionary<long, string>? result = DriftExplanations?.Invoke(items.Select(i => i.PermitId));
            return Task.FromResult(result);
        }

        public Task<AiPriceAdvice?> AdviseZonePriceAsync(ZonePriceFacts facts, CancellationToken ct)
        {
            PriceAdvices++;
            return Task.FromResult(PriceAdviceToReturn);
        }

        public Task<AiEncroachmentResult> AnalyzeInspectionPhotoAsync(string photoUrl, double? slotWidth, double? slotLength, CancellationToken ct)
        {
            var result = EncroachmentResultToReturn?.Invoke(slotWidth, slotLength)
                ?? new AiEncroachmentResult(false, 0, "[test]", [], false);
            return Task.FromResult(result);
        }

        public Task<AiIdExtractionResult> ExtractIdDocumentAsync(IReadOnlyList<WardEvidenceDto> evidence, CancellationToken ct) =>
            Task.FromResult(new AiIdExtractionResult(null, null, null, 0, false));

        public Task<AiDocumentCheckResult> CompareDeclaredProfileAsync(string declaredName, string? extractedIdNumber, string declaredAddress, AiIdExtractionResult extraction, CancellationToken ct) =>
            Task.FromResult(new AiDocumentCheckResult(0, false, true, "[test]", [], false));

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
}
