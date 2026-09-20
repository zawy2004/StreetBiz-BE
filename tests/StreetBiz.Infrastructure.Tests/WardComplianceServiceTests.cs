using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Features.WardCompliance;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Security;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// A prior draft of WardComplianceService approved rental applications with no
/// transaction at all, letting two officers double-book the same slot. These
/// tests exist specifically to guard against that regression.
/// </summary>
public sealed class WardComplianceServiceTests
{
    [Fact]
    public async Task Concurrent_approvals_for_the_same_slot_only_one_succeeds()
    {
        using var f = await Fixture.Create();

        var t1 = f.NewService().DecideRentalApplicationAsync(
            f.Actor, 1, new WardRentalApplicationDecision("APPROVE", "Đạt yêu cầu", "PENDING"), default);
        var t2 = f.NewService().DecideRentalApplicationAsync(
            f.Actor, 2, new WardRentalApplicationDecision("APPROVE", "Đạt yêu cầu", "PENDING"), default);

        var results = await Task.WhenAll(t1.ContinueWith(TryUnwrap), t2.ContinueWith(TryUnwrap));

        Assert.Single(results, r => r.Ok);
        Assert.Single(results, r => !r.Ok);

        using var verify = f.NewDb();
        var activeContracts = await verify.RentalContracts
            .CountAsync(c => c.slot_id == 100 && c.contract_status == "ACTIVE");
        Assert.Equal(1, activeContracts);

        static (bool Ok, Exception? Error) TryUnwrap(Task<WardRentalApplicationDetailDto> t) =>
            t.IsFaulted ? (false, t.Exception!.InnerException) : (true, null);
    }

    [Fact]
    public async Task Sanction_amount_always_comes_from_the_wards_own_schedule_row()
    {
        using var f = await Fixture.Create();
        var service = f.NewService();

        await service.RecordViolationAsync(f.Actor, new RecordWardViolationRequest(
            ContractId: null, SlotId: 100, VendorId: 10,
            ViolationType: "UNAUTHORIZED_BUSINESS_USE", Description: "Bày bán ngoài ranh giới ô", EvidenceUrl: null), default);

        using var read = f.NewDb();
        var violationId = (await read.Violations.SingleAsync()).violation_id;

        var result = await service.SanctionViolationAsync(f.Actor, violationId,
            new SanctionWardViolationRequest(
                PenaltyScheduleId: 1, DecisionNumber: "QD-2026-001",
                SignerName: "Trần Văn Bí", SignerTitle: "Chủ tịch UBND Phường", Notes: null), default);

        // 2,500,000 is the seeded PenaltyFeeSchedules amount -- must match exactly,
        // regardless of anything an AI legal-suggestion call might have proposed.
        Assert.Equal(2500000m, result.PenaltyAmount);
        Assert.Equal("QD-2026-001", result.SanctionDecisionNumber);
        Assert.Equal("Chủ tịch UBND Phường", result.SignerTitle);
    }

    [Fact]
    public async Task Sanctioning_with_another_wards_schedule_is_rejected()
    {
        using var f = await Fixture.Create();
        var service = f.NewService();

        await service.RecordViolationAsync(f.Actor, new RecordWardViolationRequest(
            null, 100, 10, "UNAUTHORIZED_BUSINESS_USE", "Vi phạm", null), default);

        using var read = f.NewDb();
        var violationId = (await read.Violations.SingleAsync()).violation_id;

        // penalty_schedule_id 2 belongs to ward 2, not ward 1 (f.Actor's ward).
        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.SanctionViolationAsync(f.Actor, violationId,
                new SanctionWardViolationRequest(
                    PenaltyScheduleId: 2, DecisionNumber: "QD-X",
                    SignerName: "Trần Văn Bí", SignerTitle: "Chủ tịch UBND Phường", Notes: null), default));
    }

    [Fact]
    public async Task Other_ward_cannot_read_or_decide_on_this_wards_registration()
    {
        using var f = await Fixture.Create();
        var foreignActor = new WardActor(2, 2, "Cán bộ phường khác");
        var service = f.NewService();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetEnrollmentDetailAsync(foreignActor, 1, default));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.DecideEnrollmentAsync(foreignActor, 1,
                new WardEnrollmentDecision("APPROVE", "X", "SUBMITTED"), default));
    }

    [Fact]
    public async Task Document_check_refuses_honestly_without_separate_biometric_consent()
    {
        // Luat Bao ve du lieu ca nhan 2025 / Nghi dinh 356/2025/ND-CP requires biometric-data
        // consent to be a separate, explicit action. Registration 1 in the fixture has none.
        using var f = await Fixture.Create();
        var service = f.NewService();

        var result = await service.ReRunDocumentCheckAsync(f.Actor, 1, default);

        Assert.False(result.IsAiGenerated);
        Assert.True(result.NeedsManualVerification);
        Assert.Contains(result.Discrepancies, d => d.Contains("sinh trắc học"));
    }

    [Fact]
    public async Task Risk_queue_flags_registration_with_no_evidence_and_explains_why()
    {
        using var f = await Fixture.Create();
        var service = f.NewService();

        var queue = await service.GetRiskQueueAsync(f.Actor, default);

        var flagged = Assert.Single(queue, x => x.RegistrationId == "2");
        Assert.Contains(flagged.Breakdown, b => b.Reason.Contains("minh chứng"));
        Assert.True(flagged.Score > 0);
    }

    private sealed class Fixture : IDisposable
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public WardActor Actor { get; } = new(1, 1, "Cán bộ phường");

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
                new UserAccount { user_id = 2, phone_number = "0900000002", password_hash = "x", full_name = "Cán bộ 2", role_code = "WARD_AUTHORITY", ward_unit_id = 2, account_status = "ACTIVE" },
                new UserAccount { user_id = 10, phone_number = "0900000010", password_hash = "x", full_name = "Chủ hộ A", role_code = "VENDOR", account_status = "ACTIVE" },
                new UserAccount { user_id = 11, phone_number = "0900000011", password_hash = "x", full_name = "Chủ hộ B", role_code = "VENDOR", account_status = "ACTIVE" });
            db.Vendors.AddRange(
                new Vendor { vendor_id = 10, user_id = 10 },
                new Vendor { vendor_id = 11, user_id = 11 });
            db.BusinessRegistrations.AddRange(
                new BusinessRegistration { registration_id = 1, vendor_id = 10, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Hộ A", registration_status = "APPROVED" },
                new BusinessRegistration { registration_id = 2, vendor_id = 11, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Hộ B", registration_status = "SUBMITTED" });
            db.PricingZones.Add(new PricingZone { zone_id = 1, ward_unit_id = 1, zone_name = "Khu A", price_per_day = 50000, created_by = 1 });
            db.SidewalkSlots.Add(new SidewalkSlot { slot_id = 100, zone_id = 1, slot_code = "A-01", source = "WARD_DEFINED", slot_status = "PENDING_APPLICATION", latitude = 10, longitude = 106 });
            db.RentalApplications.AddRange(
                new RentalApplication { application_id = 1, registration_id = 1, slot_id = 100, application_method = "MANUAL_SELECTED", application_status = "PENDING", requested_term_days = 30 },
                new RentalApplication { application_id = 2, registration_id = 1, slot_id = 100, application_method = "MANUAL_SELECTED", application_status = "PENDING", requested_term_days = 30 });
            db.ViolationTypes.Add(new ViolationType { violation_type_code = "UNAUTHORIZED_BUSINESS_USE", description = "Sử dụng trái phép vỉa hè", is_active = true });
            db.PenaltyFeeSchedules.AddRange(
                new PenaltyFeeSchedule { penalty_schedule_id = 1, ward_unit_id = 1, violation_type = "UNAUTHORIZED_BUSINESS_USE", penalty_amount = 2500000, legal_basis = "Nghị định 168/2024/NĐ-CP", created_by = 1 },
                new PenaltyFeeSchedule { penalty_schedule_id = 2, ward_unit_id = 2, violation_type = "UNAUTHORIZED_BUSINESS_USE", penalty_amount = 9999999, legal_basis = "Khung khác phường 2", created_by = 2 });

            await db.SaveChangesAsync();
            return f;
        }

        public TestContext NewDb() =>
            new(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(Connection).Options);

        public WardComplianceService NewService() =>
            new(NewDb(),
                new PermitTokenService(Options.Create(new PermitSettings { SigningKey = "test-only-signing-key-0123456789" })),
                new NoOpAiComplianceService(),
                TimeProvider.System);

        public void Dispose() => Connection.Dispose();
    }

    /// Deterministic no-op double: every test in this file exercises DB/business-rule
    /// behavior, not the AI provider (see AiComplianceServiceTests for that).
    private sealed class NoOpAiComplianceService : IAiComplianceService
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
            Task.FromResult("[test] Trợ lý StreetBiz sẵn sàng hỗ trợ.");
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
