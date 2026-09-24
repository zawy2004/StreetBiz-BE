using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Features.WardCompliance;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Common;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
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
    public async Task Approving_an_enrollment_without_manual_identity_verification_is_blocked()
    {
        // BR-41: AI-OCR (id_number/AiComplianceService) only reads and self-compares an
        // uploaded photo -- it never queries the national population database -- so it can
        // never by itself satisfy the KYC gate. Only ConfirmIdentityAsync can.
        using var f = await Fixture.Create();
        var service = f.NewService();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.DecideEnrollmentAsync(f.Actor, 2,
                new WardEnrollmentDecision("APPROVE", "Đạt yêu cầu", "SUBMITTED"), default));

        Assert.Contains("đối chiếu CCCD", ex.Message);
    }

    [Fact]
    public async Task Confirming_identity_then_approving_succeeds_and_is_reflected_on_the_detail()
    {
        using var f = await Fixture.Create();
        var service = f.NewService();

        var confirmed = await service.ConfirmIdentityAsync(f.Actor, 2,
            new ConfirmEnrollmentIdentity("Đối chiếu trực tiếp tại UBND phường ngày 20/09/2026"), default);
        Assert.True(confirmed.IdentityVerified);

        var result = await service.DecideEnrollmentAsync(f.Actor, 2,
            new WardEnrollmentDecision("APPROVE", "Đạt yêu cầu", "SUBMITTED"), default);

        Assert.Equal("APPROVED", result.Status);
        Assert.True(result.IdentityVerified);
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
    public async Task Opening_an_enrollment_again_reuses_the_cached_ai_check()
    {
        // Every open used to call the provider twice (~3.5 s, paid credits).
        using var f = await Fixture.Create();
        await GiveConsentAndIdPhoto(f, registrationId: 2);
        var ai = new CountingAiComplianceService();

        var first = await f.NewService(ai).GetEnrollmentDetailAsync(f.Actor, 2, default);
        var second = await f.NewService(ai).GetEnrollmentDetailAsync(f.Actor, 2, default);

        Assert.Equal(1, ai.Extractions);
        Assert.Equal(1, ai.Comparisons);
        Assert.Equal(first.AiCheck!.Summary, second.AiCheck!.Summary);
        Assert.True(second.AiCheck!.IsAiGenerated);
    }

    [Fact]
    public async Task Explicit_rerun_and_changed_evidence_both_bypass_the_cached_ai_check()
    {
        using var f = await Fixture.Create();
        await GiveConsentAndIdPhoto(f, registrationId: 2);
        var ai = new CountingAiComplianceService();

        await f.NewService(ai).GetEnrollmentDetailAsync(f.Actor, 2, default);
        await f.NewService(ai).ReRunDocumentCheckAsync(f.Actor, 2, default);
        Assert.Equal(2, ai.Comparisons);

        using (var db = f.NewDb())
        {
            (await db.RegistrationEvidences.SingleAsync(e => e.registration_id == 2)).file_url = "/uploads/cccd-new.jpg";
            await db.SaveChangesAsync();
        }

        await f.NewService(ai).GetEnrollmentDetailAsync(f.Actor, 2, default);
        Assert.Equal(3, ai.Comparisons);
    }

    [Fact]
    public async Task A_fallback_result_is_not_cached_so_the_next_open_retries_the_provider()
    {
        using var f = await Fixture.Create();
        await GiveConsentAndIdPhoto(f, registrationId: 2);
        var ai = new CountingAiComplianceService { ProviderDown = true };

        await f.NewService(ai).GetEnrollmentDetailAsync(f.Actor, 2, default);
        await f.NewService(ai).GetEnrollmentDetailAsync(f.Actor, 2, default);

        Assert.Equal(2, ai.Comparisons);
        using var db = f.NewDb();
        Assert.Null((await db.BusinessRegistrations.SingleAsync(r => r.registration_id == 2)).ai_check_result);
    }

    private static async Task GiveConsentAndIdPhoto(Fixture f, long registrationId)
    {
        using var db = f.NewDb();
        (await db.BusinessRegistrations.SingleAsync(r => r.registration_id == registrationId))
            .biometric_consent_at = DateTime.UtcNow;
        db.RegistrationEvidences.Add(new RegistrationEvidence
        {
            registration_id = registrationId,
            evidence_type = "IDENTITY_DOCUMENT",
            file_url = "/uploads/cccd.jpg",
            uploaded_at = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("VALID", true)]
    [InlineData("NOT_YET_VALID", false)]
    [InlineData("SUSPENDED", false)]
    [InlineData("EXPIRED", false)]
    [InlineData("REVOKED", false)]
    [InlineData("ACTIVE", false)] // the old (wrong) comparison target -- must NOT be treated as valid
    [InlineData(null, false)]
    public void Permit_validity_matches_the_real_effective_status_enum_not_ACTIVE(string? effectiveStatus, bool expectedValid)
    {
        // Regression for a bug where InspectPermitAsync compared effective_status against
        // "ACTIVE" -- a value that column can never actually hold -- so every scanned permit,
        // including genuinely valid ones, always reported as invalid.
        Assert.Equal(expectedValid, WardComplianceService.IsPermitEffectivelyValid(effectiveStatus));
    }

    [Fact]
    public async Task Approving_an_application_is_blocked_when_the_slots_existing_contract_is_only_suspended()
    {
        // Regression: a SUSPENDED contract is still legally in force (not
        // cancelled/expired/revoked); a prior version only checked for ACTIVE, letting a
        // second application be approved for a slot whose occupant was merely suspended.
        using var f = await Fixture.Create();

        using (var seed = f.NewDb())
        {
            seed.RentalContracts.Add(new RentalContract
            {
                contract_id = 900, application_id = 1, slot_id = 100, vendor_id = 10,
                start_date = DateOnly.FromDateTime(DateTime.UtcNow),
                end_date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
                contract_status = "SUSPENDED", created_at = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var service = f.NewService();
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.DecideRentalApplicationAsync(f.Actor, 1,
                new WardRentalApplicationDecision("APPROVE", "Đạt yêu cầu", "PENDING"), default));
    }

    [Fact]
    public async Task Revoking_a_permit_frees_the_slot_and_marks_the_contract_revoked()
    {
        // Regression: ExecutePermitActionAsync used to only write DigitalPermit.permit_status,
        // leaving RentalContract stuck at ACTIVE and the slot permanently occupied even after
        // the permit backing it was revoked.
        using var f = await Fixture.Create();

        long permitId;
        using (var seed = f.NewDb())
        {
            seed.RentalContracts.Add(new RentalContract
            {
                contract_id = 901, application_id = 1, slot_id = 100, vendor_id = 10,
                start_date = DateOnly.FromDateTime(DateTime.UtcNow),
                end_date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
                contract_status = "ACTIVE", created_at = DateTime.UtcNow
            });
            seed.SidewalkSlots.First(s => s.slot_id == 100).slot_status = "ACTIVE";
            await seed.SaveChangesAsync();

            seed.DigitalPermits.Add(new DigitalPermit
            {
                permit_id = 950, contract_id = 901, qr_payload = "test-payload",
                permit_status = "ACTIVE", issued_at = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
            permitId = 950;
        }

        var service = f.NewService();
        await service.ExecutePermitActionAsync(f.Actor, permitId,
            new WardPermitActionRequest("REVOKE", "Vi phạm nghiêm trọng"), default);

        using var verify = f.NewDb();
        var contract = await verify.RentalContracts.SingleAsync(c => c.contract_id == 901);
        var slot = await verify.SidewalkSlots.SingleAsync(s => s.slot_id == 100);
        Assert.Equal("REVOKED", contract.contract_status);
        Assert.Equal("AVAILABLE", slot.slot_status);
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

    [Fact]
    public async Task Approving_renewal_extends_contract_end_date_and_creates_new_fee_schedule_revision()
    {
        using var f = await Fixture.Create();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var initialEndDate = today.AddDays(30);

        using (var seed = f.NewDb())
        {
            seed.RentalContracts.Add(new RentalContract
            {
                contract_id = 501,
                application_id = 1,
                slot_id = 100,
                vendor_id = 10,
                start_date = today,
                end_date = initialEndDate,
                contract_status = "ACTIVE",
                created_at = DateTime.UtcNow
            });
            seed.FeeSchedules.Add(new FeeSchedule
            {
                fee_schedule_id = 501,
                contract_id = 501,
                revision = 1,
                total_amount = 1500000,
                generated_at = DateTime.UtcNow
            });
            seed.DigitalPermits.Add(new DigitalPermit
            {
                permit_id = 501,
                contract_id = 501,
                qr_payload = "qr-renewal-501",
                permit_status = "ACTIVE",
                issued_at = DateTime.UtcNow
            });
            seed.RenewalRequests.Add(new RenewalRequest
            {
                renewal_id = 601,
                contract_id = 501,
                requested_term_days = 30,
                renewal_status = "PENDING",
                created_at = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var service = f.NewService();
        var detail = await service.GetRenewalDetailAsync(f.Actor, 601, default);
        Assert.True(detail.CanApprove);
        Assert.True(detail.IsFastTrackEligible);
        Assert.Equal(initialEndDate, detail.CurrentEndDate);
        Assert.Equal(initialEndDate.AddDays(30), detail.ProposedEndDate);

        var result = await service.DecideRenewalAsync(
            f.Actor, 601, new WardRenewalDecision("APPROVE", "Đủ điều kiện theo quy định", "PENDING"), default);

        Assert.Equal("APPROVED", result.Status);

        using var verify = f.NewDb();
        var contract = await verify.RentalContracts.SingleAsync(c => c.contract_id == 501);
        Assert.Equal(initialEndDate.AddDays(30), contract.end_date);

        var renewal = await verify.RenewalRequests.SingleAsync(r => r.renewal_id == 601);
        Assert.Equal("APPROVED", renewal.renewal_status);
        Assert.Equal(initialEndDate.AddDays(30), renewal.new_end_date);
        Assert.Equal(1, renewal.reviewed_by);

        var oldFee = await verify.FeeSchedules.SingleAsync(fs => fs.fee_schedule_id == 501);
        Assert.NotNull(oldFee.superseded_at);

        var newFee = await verify.FeeSchedules.SingleAsync(fs => fs.contract_id == 501 && fs.revision == 2);
        Assert.Equal(1500000, newFee.total_amount);
        Assert.Null(newFee.superseded_at);

        var audit = await verify.AuditLogs.SingleAsync(a => a.action == "RENEWAL_APPROVED");
        Assert.Equal(601, audit.entity_id);
        Assert.Contains("501", audit.details);
    }

    [Fact]
    public async Task Renewal_review_uses_registration_linked_to_the_contract_application()
    {
        using var f = await Fixture.Create();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using (var seed = f.NewDb())
        {
            seed.BusinessRegistrations.Add(new BusinessRegistration
            {
                registration_id = 3,
                vendor_id = 10,
                ward_unit_id = 1,
                vendor_type = "ITINERANT",
                display_name = "Hồ sơ chưa duyệt của hộ A",
                registration_status = "SUBMITTED"
            });
            seed.RentalApplications.Add(new RentalApplication
            {
                application_id = 3,
                registration_id = 3,
                slot_id = 100,
                application_method = "MANUAL_SELECTED",
                application_status = "APPROVED",
                requested_term_days = 30
            });
            seed.RentalContracts.Add(new RentalContract
            {
                contract_id = 505,
                application_id = 3,
                slot_id = 100,
                vendor_id = 10,
                start_date = today,
                end_date = today.AddDays(30),
                contract_status = "ACTIVE",
                created_at = DateTime.UtcNow
            });
            seed.DigitalPermits.Add(new DigitalPermit
            {
                permit_id = 505,
                contract_id = 505,
                qr_payload = "qr-renewal-505",
                permit_status = "ACTIVE",
                issued_at = DateTime.UtcNow
            });
            seed.RenewalRequests.Add(new RenewalRequest
            {
                renewal_id = 605,
                contract_id = 505,
                requested_term_days = 30,
                renewal_status = "PENDING",
                created_at = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var service = f.NewService();
        var detail = await service.GetRenewalDetailAsync(f.Actor, 605, default);

        Assert.Equal(3, detail.RegistrationId);
        Assert.Equal("SUBMITTED", detail.RegistrationStatus);
        Assert.False(detail.CanApprove);
        Assert.Contains(detail.Blockers, b => b.Contains("BR-16", StringComparison.OrdinalIgnoreCase));

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.DecideRenewalAsync(
                f.Actor,
                605,
                new WardRenewalDecision("APPROVE", "Không được duyệt nhầm hồ sơ khác", "PENDING"),
                default));
    }

    [Fact]
    public async Task Approving_renewal_is_blocked_when_penalty_is_unpaid()
    {
        using var f = await Fixture.Create();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using (var seed = f.NewDb())
        {
            seed.RentalContracts.Add(new RentalContract
            {
                contract_id = 506,
                application_id = 1,
                slot_id = 100,
                vendor_id = 10,
                start_date = today,
                end_date = today.AddDays(30),
                contract_status = "ACTIVE",
                created_at = DateTime.UtcNow
            });
            seed.DigitalPermits.Add(new DigitalPermit
            {
                permit_id = 506,
                contract_id = 506,
                qr_payload = "qr-renewal-506",
                permit_status = "ACTIVE",
                issued_at = DateTime.UtcNow
            });
            seed.RenewalRequests.Add(new RenewalRequest
            {
                renewal_id = 606,
                contract_id = 506,
                requested_term_days = 30,
                renewal_status = "PENDING",
                created_at = DateTime.UtcNow
            });
            seed.Violations.Add(new Violation
            {
                violation_id = 706,
                contract_id = 506,
                slot_id = 100,
                vendor_id = 10,
                violation_type = "UNAUTHORIZED_BUSINESS_USE",
                description = "Lấn chiếm lối đi",
                recorded_by = 1,
                recorder_role = "WARD_AUTHORITY",
                source = "WARD_INSPECTION",
                recorded_at = DateTime.UtcNow
            });
            seed.Penalties.Add(new Penalty
            {
                penalty_id = 706,
                violation_id = 706,
                penalty_schedule_id = 1,
                amount = 2500000,
                penalty_status = "UNPAID",
                decision_number = "QD-706",
                signer_name = "Chủ tịch UBND Phường",
                signer_title = "Chủ tịch",
                created_at = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var service = f.NewService();
        var detail = await service.GetRenewalDetailAsync(f.Actor, 606, default);

        Assert.False(detail.CanApprove);
        Assert.Equal(1, detail.Scorecard.UnpaidPenaltyCount);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.DecideRenewalAsync(
                f.Actor,
                606,
                new WardRenewalDecision("APPROVE", "Đủ điều kiện", "PENDING"),
                default));
        Assert.Contains("chưa hoàn thành nộp phạt", ex.Message);
    }

    /// <summary>Mirrors HasOutstandingDebtAsync, the same debt definition that already blocks
    /// SIDE-07 (return slot) and BR-27 (slot transfer): renewal approval must not let a vendor
    /// who owes overdue rent for the current term get that term extended for free.</summary>
    [Fact]
    public async Task Approving_renewal_is_blocked_when_a_regular_fee_item_is_overdue()
    {
        using var f = await Fixture.Create();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using (var seed = f.NewDb())
        {
            seed.RentalContracts.Add(new RentalContract
            {
                contract_id = 507,
                application_id = 1,
                slot_id = 100,
                vendor_id = 10,
                start_date = today,
                end_date = today.AddDays(30),
                contract_status = "ACTIVE",
                created_at = DateTime.UtcNow
            });
            seed.DigitalPermits.Add(new DigitalPermit
            {
                permit_id = 507,
                contract_id = 507,
                qr_payload = "qr-renewal-507",
                permit_status = "ACTIVE",
                issued_at = DateTime.UtcNow
            });
            seed.RenewalRequests.Add(new RenewalRequest
            {
                renewal_id = 607,
                contract_id = 507,
                requested_term_days = 30,
                renewal_status = "PENDING",
                created_at = DateTime.UtcNow
            });
            seed.FeeSchedules.Add(new FeeSchedule
            {
                fee_schedule_id = 507,
                contract_id = 507,
                revision = 1,
                total_amount = 1500000,
                generated_at = DateTime.UtcNow
            });
            seed.FeeScheduleItems.Add(new FeeScheduleItem
            {
                fee_item_id = 707,
                fee_schedule_id = 507,
                due_date = today.AddDays(-5),
                amount = 1500000,
                item_status = "OVERDUE"
            });
            await seed.SaveChangesAsync();
        }

        var service = f.NewService();
        var detail = await service.GetRenewalDetailAsync(f.Actor, 607, default);

        Assert.False(detail.CanApprove);
        Assert.False(detail.IsFastTrackEligible);
        Assert.Contains(detail.Blockers, b => b.Contains("quá hạn", StringComparison.OrdinalIgnoreCase));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.DecideRenewalAsync(
                f.Actor,
                607,
                new WardRenewalDecision("APPROVE", "Đủ điều kiện", "PENDING"),
                default));
        Assert.Contains("quá hạn", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejecting_renewal_leaves_contract_end_date_untouched_and_records_reason()
    {
        using var f = await Fixture.Create();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var initialEndDate = today.AddDays(30);

        using (var seed = f.NewDb())
        {
            seed.RentalContracts.Add(new RentalContract
            {
                contract_id = 502,
                application_id = 1,
                slot_id = 100,
                vendor_id = 10,
                start_date = today,
                end_date = initialEndDate,
                contract_status = "ACTIVE",
                created_at = DateTime.UtcNow
            });
            seed.RenewalRequests.Add(new RenewalRequest
            {
                renewal_id = 602,
                contract_id = 502,
                requested_term_days = 30,
                renewal_status = "PENDING",
                created_at = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var service = f.NewService();
        var result = await service.DecideRenewalAsync(
            f.Actor, 602, new WardRenewalDecision("REJECT", "Tuyến phố chuẩn bị chỉnh trang hạ tầng", "PENDING"), default);

        Assert.Equal("REJECTED", result.Status);

        using var verify = f.NewDb();
        var contract = await verify.RentalContracts.SingleAsync(c => c.contract_id == 502);
        Assert.Equal(initialEndDate, contract.end_date); // Untouched

        var renewal = await verify.RenewalRequests.SingleAsync(r => r.renewal_id == 602);
        Assert.Equal("REJECTED", renewal.renewal_status);
        Assert.Equal("Tuyến phố chuẩn bị chỉnh trang hạ tầng", renewal.review_decision_reason);
    }

    [Fact]
    public async Task Other_ward_cannot_review_or_decide_on_renewal()
    {
        using var f = await Fixture.Create();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using (var seed = f.NewDb())
        {
            seed.RentalContracts.Add(new RentalContract
            {
                contract_id = 503,
                application_id = 1,
                slot_id = 100, // Ward 1
                vendor_id = 10,
                start_date = today,
                end_date = today.AddDays(30),
                contract_status = "ACTIVE",
                created_at = DateTime.UtcNow
            });
            seed.RenewalRequests.Add(new RenewalRequest
            {
                renewal_id = 603,
                contract_id = 503,
                requested_term_days = 30,
                renewal_status = "PENDING",
                created_at = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var otherWardActor = new WardActor(2, 2, "Cán bộ Phường 2");
        var service = f.NewService();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetRenewalDetailAsync(otherWardActor, 603, default));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.DecideRenewalAsync(otherWardActor, 603, new WardRenewalDecision("APPROVE", "Lý do", "PENDING"), default));
    }

    [Fact]
    public async Task Concurrent_renewal_decisions_handled_honestly_via_expected_status()
    {
        // Two officers open the same renewal, both see PENDING, both decide "APPROVE" against
        // that same expected status. Only one may win -- the loser must get an honest 409
        // Conflict (ConflictException) rather than a silently overwritten decision.
        using var f = await Fixture.Create();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using (var seed = f.NewDb())
        {
            seed.RentalContracts.Add(new RentalContract
            {
                contract_id = 504,
                application_id = 1,
                slot_id = 100,
                vendor_id = 10,
                start_date = today,
                end_date = today.AddDays(30),
                contract_status = "ACTIVE",
                created_at = DateTime.UtcNow
            });
            seed.DigitalPermits.Add(new DigitalPermit
            {
                permit_id = 504,
                contract_id = 504,
                qr_payload = "qr-renewal-504",
                permit_status = "ACTIVE",
                issued_at = DateTime.UtcNow
            });
            seed.RenewalRequests.Add(new RenewalRequest
            {
                renewal_id = 604,
                contract_id = 504,
                requested_term_days = 30,
                renewal_status = "PENDING",
                created_at = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var t1 = f.NewService().DecideRenewalAsync(
            f.Actor, 604, new WardRenewalDecision("APPROVE", "Đạt yêu cầu", "PENDING"), default);
        var t2 = f.NewService().DecideRenewalAsync(
            f.Actor, 604, new WardRenewalDecision("APPROVE", "Đạt yêu cầu", "PENDING"), default);

        var results = await Task.WhenAll(t1.ContinueWith(TryUnwrap), t2.ContinueWith(TryUnwrap));

        Assert.Single(results, r => r.Ok);
        Assert.Single(results, r => !r.Ok && r.Error is ConflictException);

        using var verify = f.NewDb();
        var renewal = await verify.RenewalRequests.SingleAsync(r => r.renewal_id == 604);
        Assert.Equal("APPROVED", renewal.renewal_status);

        static (bool Ok, Exception? Error) TryUnwrap(Task<WardRenewalDetailDto> t) =>
            t.IsFaulted ? (false, t.Exception!.InnerException) : (true, null);
    }

    [Fact]
    public async Task Batch_approve_processes_each_renewal_independently_and_reports_per_item_failures()
    {
        // Idea 4 (WARD-09): a batch call is "click Approve N times" with one shared reason, not
        // a shortcut around per-item checks -- one stale item must not abort the rest, and each
        // outcome is reported back individually rather than the whole call failing 409/404.
        using var f = await Fixture.Create();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using (var seed = f.NewDb())
        {
            seed.RentalContracts.Add(new RentalContract
            {
                contract_id = 505,
                application_id = 1,
                slot_id = 100,
                vendor_id = 10,
                start_date = today,
                end_date = today.AddDays(30),
                contract_status = "ACTIVE",
                created_at = DateTime.UtcNow
            });
            seed.DigitalPermits.Add(new DigitalPermit
            {
                permit_id = 605,
                contract_id = 505,
                qr_payload = "qr-renewal-605",
                permit_status = "ACTIVE",
                issued_at = DateTime.UtcNow
            });
            seed.RenewalRequests.Add(new RenewalRequest
            {
                renewal_id = 605,
                contract_id = 505,
                requested_term_days = 15,
                renewal_status = "PENDING",
                created_at = DateTime.UtcNow
            });
            seed.RentalContracts.Add(new RentalContract
            {
                contract_id = 506,
                application_id = 2,
                slot_id = 100,
                vendor_id = 10,
                start_date = today,
                end_date = today.AddDays(30),
                contract_status = "ACTIVE",
                created_at = DateTime.UtcNow
            });
            seed.DigitalPermits.Add(new DigitalPermit
            {
                permit_id = 606,
                contract_id = 506,
                qr_payload = "qr-renewal-606",
                permit_status = "ACTIVE",
                issued_at = DateTime.UtcNow
            });
            // Actually PENDING, but the batch request below claims it as UNDER_REVIEW -- the
            // stand-in for "another officer already touched this one".
            seed.RenewalRequests.Add(new RenewalRequest
            {
                renewal_id = 606,
                contract_id = 506,
                requested_term_days = 15,
                renewal_status = "PENDING",
                created_at = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var service = f.NewService();
        var batchResult = await service.BatchDecideRenewalsAsync(
            f.Actor,
            new WardRenewalBatchDecisionRequest(
                Items:
                [
                    new WardRenewalBatchDecisionItemRequest(605, "PENDING"),
                    new WardRenewalBatchDecisionItemRequest(606, "UNDER_REVIEW"),
                ],
                Decision: "APPROVE",
                Reason: "Đủ điều kiện gia hạn theo quy định, điểm bán chấp hành tốt quy chế hè phố"),
            default);

        Assert.Equal(2, batchResult.TotalRequested);
        Assert.Equal(1, batchResult.SuccessCount);
        Assert.Equal(1, batchResult.FailureCount);

        var succeeded = Assert.Single(batchResult.Results, r => r.RenewalId == 605);
        Assert.True(succeeded.Success);
        Assert.Equal(today.AddDays(30).AddDays(15), succeeded.NewEndDate);

        var failed = Assert.Single(batchResult.Results, r => r.RenewalId == 606);
        Assert.False(failed.Success);
        Assert.NotNull(failed.ErrorMessage);
        Assert.Null(failed.NewEndDate);

        using var verify = f.NewDb();
        Assert.Equal("APPROVED", (await verify.RenewalRequests.SingleAsync(r => r.renewal_id == 605)).renewal_status);
        // The failed item must be untouched, not half-applied.
        Assert.Equal("PENDING", (await verify.RenewalRequests.SingleAsync(r => r.renewal_id == 606)).renewal_status);
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

        public WardComplianceService NewService(IAiComplianceService? ai = null)
        {
            // One context per service, shared with its KYC repository, so both see the same
            // change tracker -- mirrors the scoped lifetimes in DependencyInjection.
            var db = NewDb();
            return new WardComplianceService(
                db,
                new PermitTokenService(Options.Create(new PermitSettings { SigningKey = "test-only-signing-key-0123456789" })),
                ai ?? new NoOpAiComplianceService(),
                new KycResultRepository(db, new DateTimeProvider()),
                TimeProvider.System);
        }

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

    /// Counts provider calls; `ProviderDown` makes it answer like the real service does
    /// when the AI provider is unreachable (IsAiGenerated = false).
    private sealed class CountingAiComplianceService : IAiComplianceService
    {
        private readonly NoOpAiComplianceService inner = new();
        public int Extractions { get; private set; }
        public int Comparisons { get; private set; }
        public bool ProviderDown { get; init; }

        public Task<AiIdExtractionResult> ExtractIdDocumentAsync(IReadOnlyList<WardEvidenceDto> evidence, CancellationToken ct)
        {
            Extractions++;
            return Task.FromResult(new AiIdExtractionResult("001200000001", "Hộ B", null, 95, !ProviderDown));
        }

        public Task<AiDocumentCheckResult> CompareDeclaredProfileAsync(string declaredName, string? extractedIdNumber, string declaredAddress, AiIdExtractionResult extraction, CancellationToken ct)
        {
            Comparisons++;
            return Task.FromResult(new AiDocumentCheckResult(
                92, true, false, $"check #{Comparisons}", Array.Empty<string>(), IsAiGenerated: !ProviderDown));
        }

        public Task<AiEncroachmentResult> AnalyzeInspectionPhotoAsync(string photoUrl, double? slotWidth, double? slotLength, CancellationToken ct) =>
            inner.AnalyzeInspectionPhotoAsync(photoUrl, slotWidth, slotLength, ct);

        public Task<AiLegalSuggestion> ClassifyAndDraftAsync(string? description, IReadOnlyList<PenaltyScheduleItemDto> availableSchedules, CancellationToken ct) =>
            inner.ClassifyAndDraftAsync(description, availableSchedules, ct);

        public Task<string> AnswerVendorAssistantAsync(string question, string? context, CancellationToken ct) =>
            inner.AnswerVendorAssistantAsync(question, context, ct);
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
