using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.VendorKyc;
using StreetBiz.Application.Features.WardCompliance;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Services;

public sealed class WardComplianceService(
    StreetBizDbContext db,
    IPermitTokenService permitTokens,
    IAiComplianceService aiService,
    IKycResultRepository kycResults,
    TimeProvider clock)
    : IWardComplianceService
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// Serializable decisions prevent double review and, critically, two officers
    /// approving two different applications for the same slot at once (see
    /// WardSlots.Write, whose pattern this mirrors for the same reason).
    private async Task Write(Func<Task> action, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await action();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
    }

    #region Enrollment / Registration Review
    public async Task<IReadOnlyList<WardEnrollmentListItemDto>> ListEnrollmentsAsync(
        WardActor actor,
        string? status,
        int page,
        CancellationToken ct)
    {
        var query = db.BusinessRegistrations.AsNoTracking()
            .Include(x => x.vendor).ThenInclude(v => v.user)
            .Where(x => x.ward_unit_id == actor.WardId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(x => x.registration_status == status);
        }

        var offset = (page - 1) * 20;
        var items = await query
            .OrderByDescending(x => x.created_at)
            .Skip(offset)
            .Take(20)
            .Select(x => new WardEnrollmentListItemDto(
                Id(x.registration_id),
                x.display_name,
                x.vendor.user.full_name ?? x.display_name,
                x.id_number,
                x.vendor_type,
                x.registration_status,
                x.declared_address ?? "Chưa có địa chỉ",
                x.created_at,
                x.fast_track_flag))
            .ToListAsync(ct);

        return items;
    }

    public async Task<WardEnrollmentDetailDto> GetEnrollmentDetailAsync(
        WardActor actor,
        long registrationId,
        CancellationToken ct)
    {
        // Tracked (not AsNoTracking): a first-time AI-OCR backfill of id_number
        // below is persisted here, so subsequent opens of the same record reuse
        // the stored value instead of re-calling the AI provider every time.
        var reg = await db.BusinessRegistrations
            .Include(x => x.vendor).ThenInclude(v => v.user)
            .Include(x => x.RegistrationEvidences)
            .Include(x => x.HouseholdMembers)
            // Two collections in one JOIN return evidence x members rows; split them.
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.registration_id == registrationId, ct);

        if (reg is null || reg.ward_unit_id != actor.WardId)
        {
            throw new NotFoundException("Không tìm thấy hồ sơ đăng ký điểm bán tại địa bàn phường của bạn.");
        }

        var evidence = reg.RegistrationEvidences
            .Select(e => new WardEvidenceDto(e.evidence_id, e.evidence_type, e.evidence_type, e.file_url))
            .ToList();

        var ownerName = reg.vendor?.user?.full_name ?? reg.display_name;

        // AI Document Inspector [BR-41/42]: extract real CCCD data from the
        // uploaded ID photo (multimodal), never compare against phone number.
        var aiCheck = await RunDocumentCheckAsync(reg, evidence, ownerName, refresh: false, ct);

        return new WardEnrollmentDetailDto(
            Id(reg.registration_id),
            reg.display_name,
            ownerName,
            reg.id_number,
            reg.vendor_type,
            reg.registration_status,
            reg.declared_address ?? "",
            reg.address_latitude.HasValue ? (double)reg.address_latitude.Value : null,
            reg.address_longitude.HasValue ? (double)reg.address_longitude.Value : null,
            reg.created_at,
            reg.review_decision_reason,
            reg.reviewed_by?.ToString(CultureInfo.InvariantCulture),
            reg.reviewed_at,
            evidence,
            aiCheck,
            new WardOwnerProfileDto(
                reg.owner_date_of_birth, reg.owner_gender, reg.owner_ethnicity, reg.owner_nationality,
                reg.id_type, reg.id_issued_date, reg.id_issued_place, reg.permanent_address, reg.contact_address),
            new WardBusinessProfileDto(
                reg.business_line, reg.business_line_code, reg.capital_amount, reg.labor_count, reg.planned_start_date),
            reg.food_safety_commitment_at,
            reg.HouseholdMembers.Select(m => new WardHouseholdMemberDto(
                m.full_name, m.date_of_birth, m.id_number, m.relationship_to_owner, m.capital_contribution)).ToList(),
            reg.identity_verified_at.HasValue,
            reg.identity_verified_at,
            reg.identity_verified_by?.ToString(CultureInfo.InvariantCulture),
            reg.identity_verification_note,
            await kycResults.ListForRegistrationAsync(reg.registration_id, ct));
    }

    public async Task<WardEnrollmentDetailDto> ConfirmIdentityAsync(
        WardActor actor,
        long registrationId,
        ConfirmEnrollmentIdentity request,
        CancellationToken ct)
    {
        var reg = await db.BusinessRegistrations
            .SingleOrDefaultAsync(x => x.registration_id == registrationId, ct);

        if (reg is null || reg.ward_unit_id != actor.WardId)
        {
            throw new NotFoundException("Không tìm thấy hồ sơ đăng ký điểm bán tại địa bàn phường của bạn.");
        }

        reg.identity_verified_by = actor.UserId;
        reg.identity_verified_at = Now;
        reg.identity_verification_note = request.Note.Trim();

        // Audit Log [BR-46]: this confirmation is what actually satisfies BR-41's human-in-
        // the-loop requirement for the APPROVE decision, so it must be independently auditable.
        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actor.UserId,
            action = "ENROLLMENT_IDENTITY_VERIFIED",
            entity_type = "BusinessRegistration",
            entity_id = reg.registration_id,
            details = $"Cán bộ {actor.Name} xác nhận đã đối chiếu CCCD thủ công. Ghi chú: {request.Note.Trim()}",
            created_at = Now
        });

        await db.SaveChangesAsync(ct);
        return await GetEnrollmentDetailAsync(actor, registrationId, ct);
    }

    public async Task<AiDocumentCheckResult> ReRunDocumentCheckAsync(
        WardActor actor,
        long registrationId,
        CancellationToken ct)
    {
        var reg = await db.BusinessRegistrations
            .Include(x => x.vendor).ThenInclude(v => v.user)
            .Include(x => x.RegistrationEvidences)
            .SingleOrDefaultAsync(x => x.registration_id == registrationId, ct);

        if (reg is null || reg.ward_unit_id != actor.WardId)
        {
            throw new NotFoundException("Không tìm thấy hồ sơ đăng ký điểm bán tại địa bàn phường của bạn.");
        }

        var evidence = reg.RegistrationEvidences
            .Select(e => new WardEvidenceDto(e.evidence_id, e.evidence_type, e.evidence_type, e.file_url))
            .ToList();
        var ownerName = reg.vendor?.user?.full_name ?? reg.display_name;

        // An explicit re-run is the officer asking for a fresh answer: skip the cache.
        return await RunDocumentCheckAsync(reg, evidence, ownerName, refresh: true, ct);
    }

    /// <summary>
    /// [BR-41/42] + biometric-data consent gate: refuses with an honest, non-AI result while
    /// BusinessRegistrations.biometric_consent_at is unset (Luat Bao ve du lieu ca nhan 2025 /
    /// Nghi dinh 356/2025/ND-CP requires this consent to be separate and explicit -- captured at
    /// REG-02 evidence upload, not assumed here).
    /// </summary>
    private async Task<AiDocumentCheckResult> RunDocumentCheckAsync(
        BusinessRegistration reg,
        IReadOnlyList<WardEvidenceDto> evidence,
        string ownerName,
        bool refresh,
        CancellationToken ct)
    {
        if (reg.biometric_consent_at is null)
        {
            return new AiDocumentCheckResult(
                MatchPercentage: 0,
                IsMatch: false,
                NeedsManualVerification: true,
                Summary: "[Hệ thống — chưa xác minh bằng AI] Hộ kinh doanh chưa đồng ý riêng biệt cho việc dùng OCR đối soát CCCD. Cán bộ cần đối chiếu thủ công.",
                Discrepancies: new[] { "Chưa có sự đồng ý xử lý dữ liệu sinh trắc học (Luật Bảo vệ dữ liệu cá nhân 2025)." },
                IsAiGenerated: false);
        }

        if (evidence.Count == 0)
        {
            return new AiDocumentCheckResult(
                MatchPercentage: 0,
                IsMatch: false,
                NeedsManualVerification: true,
                Summary: "[Hệ thống — chưa xác minh bằng AI] Hồ sơ chưa có ảnh minh chứng để đối soát.",
                Discrepancies: new[] { "Chưa có ảnh CCCD đính kèm." },
                IsAiGenerated: false);
        }

        // Opening a record used to call the provider twice (OCR + compare) every time:
        // ~3.5 s per page view and paid credits for an answer that cannot change until
        // the evidence or the declared data does. Reuse the stored result while its
        // inputs are unchanged.
        if (!refresh
            && TryReadCachedDocumentCheck(reg.ai_check_result, DocumentCheckKey(reg, evidence, ownerName)) is { } cached)
        {
            return cached;
        }

        var extraction = await aiService.ExtractIdDocumentAsync(evidence, ct);

        var changed = false;
        if (string.IsNullOrWhiteSpace(reg.id_number) && !string.IsNullOrWhiteSpace(extraction.IdNumber)
            && extraction.ConfidencePercent >= 85)
        {
            reg.id_number = extraction.IdNumber;
            changed = true;
        }

        var result = await aiService.CompareDeclaredProfileAsync(
            ownerName, reg.id_number, reg.declared_address ?? "", extraction, ct);

        // Only a real provider answer is cached; a fallback ("provider unavailable")
        // must be retried on the next open. The key is taken after the id_number
        // backfill so the next read, which sees the stored id_number, matches it.
        if (result.IsAiGenerated)
        {
            reg.ai_check_result = JsonSerializer.Serialize(
                new CachedDocumentCheck(DocumentCheckKey(reg, evidence, ownerName), result));
            reg.ai_checked_at = Now;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    private sealed record CachedDocumentCheck(string Key, AiDocumentCheckResult Result);

    /// <summary>Fingerprint of everything the AI check reads; any change forces a fresh check.</summary>
    private static string DocumentCheckKey(
        BusinessRegistration reg, IReadOnlyList<WardEvidenceDto> evidence, string ownerName)
    {
        var parts = new StringBuilder()
            .Append(ownerName).Append('\n')
            .Append(reg.id_number).Append('\n')
            .Append(reg.declared_address).Append('\n');
        foreach (var e in evidence.OrderBy(e => e.EvidenceId))
        {
            parts.Append(e.EvidenceId).Append('|').Append(e.Type).Append('|').Append(e.FileUrl).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parts.ToString())));
    }

    private static AiDocumentCheckResult? TryReadCachedDocumentCheck(string? json, string key)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            var cached = JsonSerializer.Deserialize<CachedDocumentCheck>(json);
            return cached is not null && cached.Key == key ? cached.Result : null;
        }
        catch (JsonException)
        {
            // An unreadable cache entry is just a miss; the check runs again and overwrites it.
            return null;
        }
    }

    public async Task<WardEnrollmentDetailDto> DecideEnrollmentAsync(
        WardActor actor,
        long registrationId,
        WardEnrollmentDecision decision,
        CancellationToken ct)
    {
        var reg = await db.BusinessRegistrations
            .SingleOrDefaultAsync(x => x.registration_id == registrationId, ct);

        if (reg is null || reg.ward_unit_id != actor.WardId)
        {
            throw new NotFoundException("Không tìm thấy hồ sơ đăng ký điểm bán tại địa bàn phường của bạn.");
        }

        if (!string.Equals(reg.registration_status, decision.ExpectedStatus, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConflictException("Hồ sơ đã được xử lý hoặc thay đổi trạng thái trước đó. Vui lòng tải lại.");
        }

        var newStatus = decision.Decision.ToUpperInvariant() switch
        {
            "APPROVE" => "APPROVED",
            "REJECT" => "REJECTED",
            "MORE_INFO" => "MORE_INFORMATION_REQUIRED",
            _ => throw new DomainRuleException("Quyết định không hợp lệ.")
        };

        // BR-41 KYC gate: AI-OCR (id_number/AiComplianceService) only reads and self-compares
        // an uploaded photo -- it never queries the Bo Cong an/CSDL quoc gia ve dan cu -- so it
        // cannot by itself prove the applicant is who they claim. An officer must have called
        // ConfirmIdentityAsync first; only then may APPROVE proceed.
        if (newStatus == "APPROVED" && reg.identity_verified_at is null)
        {
            throw new DomainRuleException(RegMessages.IdentityVerificationRequiredForApproval);
        }

        reg.registration_status = newStatus;
        reg.reviewed_by = actor.UserId;
        reg.reviewer_role = RoleCodes.WardAuthority;
        reg.reviewed_at = Now;
        reg.review_decision_reason = decision.Reason.Trim();
        reg.updated_at = Now;

        // Audit Log [BR-46]
        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actor.UserId,
            action = $"ENROLLMENT_{newStatus}",
            entity_type = "BusinessRegistration",
            entity_id = reg.registration_id,
            details = $"Cán bộ {actor.Name} quyết định: {newStatus}. Lý do: {decision.Reason.Trim()}",
            created_at = Now
        });

        var vendorUser = await db.Vendors.AsNoTracking()
            .Where(v => v.vendor_id == reg.vendor_id)
            .Select(v => v.user_id)
            .FirstOrDefaultAsync(ct);

        if (vendorUser > 0)
        {
            db.Notifications.Add(new Notification
            {
                user_id = vendorUser,
                notification_type = "ENROLLMENT_STATUS_UPDATE",
                title = newStatus == "APPROVED" ? "Hồ sơ điểm bán đã được duyệt" : "Cập nhật hồ sơ điểm bán vỉa hè",
                body = newStatus == "APPROVED"
                    ? $"Hồ sơ điểm bán '{reg.display_name}' đã được UBND Phường xác nhận đủ điều kiện trật tự đô thị."
                    : $"UBND Phường đã cập nhật trạng thái hồ sơ '{reg.display_name}': {decision.Reason.Trim()}",
                related_entity_type = "BusinessRegistration",
                related_entity_id = reg.registration_id,
                is_read = false,
                sent_at = Now
            });
        }

        await db.SaveChangesAsync(ct);
        return await GetEnrollmentDetailAsync(actor, registrationId, ct);
    }
    #endregion

    #region Rental Applications & Temporary Usage Permits
    public async Task<IReadOnlyList<WardRentalApplicationListItemDto>> ListRentalApplicationsAsync(
        WardActor actor,
        string? status,
        int page,
        CancellationToken ct)
    {
        var query = db.RentalApplications.AsNoTracking()
            .Include(x => x.slot).ThenInclude(x => x.zone)
            .Include(x => x.registration)
            .Where(x => x.slot.zone.ward_unit_id == actor.WardId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(x => x.application_status == status);
        }

        var offset = (page - 1) * 20;
        var items = await query
            .OrderByDescending(x => x.created_at)
            .Skip(offset)
            .Take(20)
            .Select(x => new WardRentalApplicationListItemDto(
                Id(x.application_id),
                x.application_method,
                x.requested_term_days,
                x.application_status,
                x.registration.display_name,
                x.slot.slot_code,
                x.slot.zone.zone_code ?? x.slot.zone.zone_name,
                x.slot.zone.price_per_day,
                x.created_at))
            .ToListAsync(ct);

        return items;
    }

    public async Task<WardRentalApplicationDetailDto> GetRentalApplicationDetailAsync(
        WardActor actor,
        long applicationId,
        CancellationToken ct)
    {
        var app = await db.RentalApplications.AsNoTracking()
            .Include(x => x.slot).ThenInclude(x => x.zone)
            .Include(x => x.registration).ThenInclude(x => x.vendor).ThenInclude(x => x.user)
            .SingleOrDefaultAsync(x => x.application_id == applicationId, ct);

        if (app is null || app.slot.zone.ward_unit_id != actor.WardId)
        {
            throw new NotFoundException("Không tìm thấy đơn xin cấp phép hè phố tại địa bàn của bạn.");
        }

        var blockers = new List<string>();

        // BR-16: Linked registration must be APPROVED
        if (app.registration.registration_status != "APPROVED")
        {
            blockers.Add("Hồ sơ đăng ký điểm kinh doanh liên kết chưa được phê duyệt (BR-16).");
        }

        if (app.slot.slot_status != "AVAILABLE" && app.slot.slot_status != "PENDING_APPLICATION")
        {
            blockers.Add($"Vị trí vỉa hè hiện không khả dụng (Trạng thái: {app.slot.slot_status}).");
        }

        // A SUSPENDED contract is still legally in force (not cancelled/expired/revoked) --
        // counting only ACTIVE here let a second application be approved for a slot whose
        // existing occupant was merely under suspension, not actually vacated.
        var hasOccupant = await db.RentalContracts.AsNoTracking()
            .AnyAsync(c => c.slot_id == app.slot_id
                && (c.contract_status == ContractStatuses.Active || c.contract_status == ContractStatuses.Suspended)
                && c.end_date >= DateOnly.FromDateTime(Now), ct);

        if (hasOccupant)
        {
            blockers.Add("Vị trí vỉa hè đã có giấy phép khác đang có hiệu lực hoạt động.");
        }

        var canApprove = app.application_status is "PENDING" or "UNDER_REVIEW" && blockers.Count == 0;

        return new WardRentalApplicationDetailDto(
            Id(app.application_id),
            app.application_method,
            app.requested_term_days,
            app.application_status,
            app.registration_id,
            app.registration.registration_status,
            app.registration.display_name,
            app.registration.vendor.user.phone_number,
            app.slot_id,
            app.slot.slot_code,
            app.slot.zone.zone_code ?? app.slot.zone.zone_name,
            app.slot.width_meters ?? 0,
            app.slot.length_meters ?? 0,
            app.slot.zone.price_per_day,
            app.created_at,
            app.review_decision_reason,
            app.reviewed_by?.ToString(CultureInfo.InvariantCulture),
            app.reviewed_at,
            canApprove,
            blockers);
    }

    public async Task<WardRentalApplicationDetailDto> DecideRentalApplicationAsync(
        WardActor actor,
        long applicationId,
        WardRentalApplicationDecision decision,
        CancellationToken ct)
    {
        // Everything below runs inside one Serializable transaction (see Write())
        // so two officers approving two different applications for the same slot
        // cannot both pass the "occupied" check before either commits.
        await Write(async () =>
        {
            var app = await db.RentalApplications
                .Include(x => x.slot).ThenInclude(x => x.zone)
                .Include(x => x.registration).ThenInclude(x => x.vendor)
                .SingleOrDefaultAsync(x => x.application_id == applicationId, ct);

            if (app is null || app.slot.zone.ward_unit_id != actor.WardId)
            {
                throw new NotFoundException("Không tìm thấy đơn xin cấp phép hè phố tại địa bàn của bạn.");
            }

            if (!string.Equals(app.application_status, decision.ExpectedStatus, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConflictException("Đơn đã được xử lý hoặc thay đổi trạng thái trước đó. Vui lòng tải lại.");
            }

            var isApprove = string.Equals(decision.Decision, "APPROVE", StringComparison.OrdinalIgnoreCase);

            if (isApprove)
            {
                if (app.registration.registration_status != "APPROVED")
                {
                    throw new DomainRuleException("Không thể cấp phép khi hồ sơ điểm bán liên kết chưa được phê duyệt (BR-16).");
                }

                // See the same fix in GetRentalApplicationDetailAsync above.
                var occupied = await db.RentalContracts
                    .AnyAsync(c => c.slot_id == app.slot_id
                        && (c.contract_status == ContractStatuses.Active || c.contract_status == ContractStatuses.Suspended)
                        && c.end_date >= DateOnly.FromDateTime(Now), ct);

                if (occupied)
                {
                    throw new ConflictException("Vị trí vỉa hè đã có giấy phép đang hoạt động.");
                }

                var startDate = DateOnly.FromDateTime(Now);
                var endDate = startDate.AddDays(app.requested_term_days);

                app.application_status = "APPROVED";
                app.reviewed_by = actor.UserId;
                app.reviewer_role = RoleCodes.WardAuthority;
                app.reviewed_at = Now;
                app.review_decision_reason = decision.Reason.Trim();

                app.slot.slot_status = "ACTIVE";

                var contract = new RentalContract
                {
                    application_id = app.application_id,
                    slot_id = app.slot_id,
                    vendor_id = app.registration.vendor_id,
                    start_date = startDate,
                    end_date = endDate,
                    contract_status = "ACTIVE",
                    created_at = Now
                };
                db.RentalContracts.Add(contract);
                // Flush within the same transaction to obtain contract_id for the
                // permit/fee rows below (OUTPUT is disabled for this table --
                // see StreetBizDbContext -- so this performs a follow-up SELECT).
                await db.SaveChangesAsync(ct);

                var qrToken = permitTokens.Create(contract.contract_id, Now);
                db.DigitalPermits.Add(new DigitalPermit
                {
                    contract_id = contract.contract_id,
                    qr_payload = qrToken,
                    permit_status = "ACTIVE",
                    issued_at = Now
                });

                var totalFee = app.requested_term_days * app.slot.zone.price_per_day;
                db.FeeSchedules.Add(new FeeSchedule
                {
                    contract_id = contract.contract_id,
                    revision = 1,
                    total_amount = totalFee,
                    generated_at = Now
                });

                db.AuditLogs.Add(new AuditLog
                {
                    actor_user_id = actor.UserId,
                    action = "PERMIT_GRANTED",
                    entity_type = "RentalApplication",
                    entity_id = app.application_id,
                    details = $"Cán bộ {actor.Name} cấp Giấy phép sử dụng tạm thời hè phố (Luật Đường bộ 2024, Điều 77; Nghị định 165/2024/NĐ-CP) tại ô {app.slot.slot_code} từ {startDate} đến {endDate}.",
                    created_at = Now
                });

                db.Notifications.Add(new Notification
                {
                    user_id = app.registration.vendor.user_id,
                    notification_type = "PERMIT_ISSUED",
                    title = "Đã được cấp phép sử dụng tạm thời hè phố",
                    body = $"UBND Phường đã phê duyệt và cấp Giấy phép sử dụng tạm thời lòng đường, vỉa hè cho ô {app.slot.slot_code}. Quý hộ kinh doanh có thể xem và tải mã QR giấy phép.",
                    related_entity_type = "RentalContract",
                    related_entity_id = contract.contract_id,
                    is_read = false,
                    sent_at = Now
                });
            }
            else
            {
                app.application_status = "REJECTED";
                app.reviewed_by = actor.UserId;
                app.reviewer_role = RoleCodes.WardAuthority;
                app.reviewed_at = Now;
                app.review_decision_reason = decision.Reason.Trim();

                if (app.slot.slot_status == "PENDING_APPLICATION")
                {
                    app.slot.slot_status = "AVAILABLE";
                }

                db.AuditLogs.Add(new AuditLog
                {
                    actor_user_id = actor.UserId,
                    action = "PERMIT_REJECTED",
                    entity_type = "RentalApplication",
                    entity_id = app.application_id,
                    details = $"Cán bộ {actor.Name} từ chối cấp phép. Lý do: {decision.Reason.Trim()}",
                    created_at = Now
                });

                db.Notifications.Add(new Notification
                {
                    user_id = app.registration.vendor.user_id,
                    notification_type = "PERMIT_APPLICATION_REJECTED",
                    title = "Đơn xin cấp phép hè phố bị từ chối",
                    body = $"UBND Phường đã từ chối cấp phép cho ô {app.slot.slot_code}. Lý do: {decision.Reason.Trim()}",
                    related_entity_type = "RentalApplication",
                    related_entity_id = app.application_id,
                    is_read = false,
                    sent_at = Now
                });
            }
        }, ct);

        return await GetRentalApplicationDetailAsync(actor, applicationId, ct);
    }
    #endregion

    #region Renewal Applications (WARD-09)
    private sealed record RenewalComplianceSnapshot(
        IReadOnlyList<string> Blockers,
        WardVendorComplianceScorecardDto Scorecard);

    private static bool IsUnpaidPenalty(Penalty penalty) =>
        penalty.penalty_status == DebtStatuses.PenaltyUnpaid;

    private static DigitalPermit? SelectCurrentPermit(IEnumerable<DigitalPermit> permits) =>
        permits
            .OrderBy(p => p.permit_status == PermitStatuses.Revoked ? 1 : 0)
            .ThenByDescending(p => p.issued_at)
            .FirstOrDefault();

    private async Task<RenewalComplianceSnapshot> BuildRenewalComplianceSnapshotAsync(
        RenewalRequest renewal,
        BusinessRegistration registration,
        CancellationToken ct)
    {
        var contract = renewal.contract;
        var blockers = new List<string>();

        if (registration.registration_status != "APPROVED")
        {
            blockers.Add("Hồ sơ đăng ký điểm kinh doanh liên kết chưa được phê duyệt hoặc đã hết hiệu lực (BR-16).");
        }

        if (contract.contract_status != ContractStatuses.Active)
        {
            blockers.Add($"Hợp đồng hiện tại không ở trạng thái ACTIVE (Trạng thái hiện tại: {contract.contract_status}).");
        }

        if (contract.slot.slot_status == SlotStatuses.Suspended)
        {
            blockers.Add("Ô hè phố đang bị tạm dừng khai thác hoặc điều chỉnh quy hoạch.");
        }

        var violations = await db.Violations.AsNoTracking()
            .Include(v => v.Penalty)
            .Where(v => v.contract_id == contract.contract_id || v.vendor_id == contract.vendor_id)
            .ToListAsync(ct);

        var penalties = violations.Where(v => v.Penalty != null).Select(v => v.Penalty!).ToList();
        var unpaidPenalties = penalties.Where(IsUnpaidPenalty).ToList();
        if (unpaidPenalties.Count > 0)
        {
            blockers.Add($"Điểm bán đang có {unpaidPenalties.Count} quyết định xử phạt vi phạm chưa hoàn thành nộp phạt.");
        }

        // Same debt definition as IRentalContractRepository.HasOutstandingDebtAsync (which
        // already blocks SIDE-07 return-slot and BR-27 slot transfer): an OVERDUE fee item is
        // unpaid rent for the current term, not a violation penalty, so it needs its own blocker
        // here -- a vendor who owes the ward money for the term they're on should not have that
        // term automatically extended.
        var hasOverdueFee = await db.FeeScheduleItems.AsNoTracking()
            .AnyAsync(i => i.fee_schedule.contract_id == contract.contract_id
                && i.item_status == DebtStatuses.FeeItemOverdue, ct);
        if (hasOverdueFee)
        {
            blockers.Add("Hợp đồng đang có khoản phí thuê hè phố quá hạn chưa thanh toán cho kỳ hiện tại.");
        }

        var permit = SelectCurrentPermit(await db.DigitalPermits.AsNoTracking()
            .Where(p => p.contract_id == contract.contract_id)
            .ToListAsync(ct));
        var permitStatus = permit?.permit_status ?? "NO_PERMIT";
        if (permit is null)
        {
            blockers.Add("Hợp đồng chưa có giấy phép số QR để gia hạn hiệu lực.");
        }
        else if (permit.permit_status == PermitStatuses.Revoked)
        {
            blockers.Add("Giấy phép sử dụng hè phố đã bị thu hồi (REVOKED).");
        }
        else if (permit.permit_status == PermitStatuses.Suspended)
        {
            blockers.Add("Giấy phép sử dụng hè phố đang bị tạm đình chỉ (SUSPENDED).");
        }

        var reports = await db.VendorReports.AsNoTracking()
            .Where(r => r.vendor_id == contract.vendor_id
                || (permit != null && r.scanned_permit_id == permit.permit_id)
                || r.slot_id == contract.slot_id)
            .ToListAsync(ct);

        var inspections = permit is not null
            ? await db.PermitScanLogs.AsNoTracking()
                .Where(s => s.permit_id == permit.permit_id && s.scan_context == "WARD_INSPECTION")
                .CountAsync(ct)
            : 0;

        var isClean = violations.Count == 0
            && unpaidPenalties.Count == 0
            && !hasOverdueFee
            && reports.Count == 0
            && registration.registration_status == "APPROVED"
            && contract.contract_status == ContractStatuses.Active
            && contract.slot.slot_status != SlotStatuses.Suspended
            && permitStatus == PermitStatuses.Active;

        var scorecard = new WardVendorComplianceScorecardDto(
            TotalInspections: inspections,
            ViolationCount: violations.Count,
            UnpaidPenaltyCount: unpaidPenalties.Count,
            TotalPenaltyAmount: penalties.Sum(p => p.amount),
            ReportCount: reports.Count,
            CurrentPermitStatus: permitStatus,
            IsCleanRecord: isClean);

        return new RenewalComplianceSnapshot(blockers, scorecard);
    }

    public async Task<IReadOnlyList<WardRenewalListItemDto>> ListRenewalsAsync(
        WardActor actor,
        string? status,
        int page,
        CancellationToken ct)
    {
        var query = db.RenewalRequests.AsNoTracking()
            .Include(x => x.contract).ThenInclude(c => c.slot).ThenInclude(s => s.zone)
            .Include(x => x.contract).ThenInclude(c => c.application).ThenInclude(a => a.registration)
            .Where(x => x.contract.slot.zone.ward_unit_id == actor.WardId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(x => x.renewal_status == status);
        }

        var offset = (page - 1) * 20;
        var renewals = await query
            .OrderByDescending(x => x.created_at)
            .Skip(offset)
            .Take(20)
            .ToListAsync(ct);

        var contractIds = renewals.Select(r => r.contract_id).Distinct().ToList();
        var vendorIds = renewals.Select(r => r.contract.vendor_id).Distinct().ToList();
        var slotIds = renewals.Select(r => r.contract.slot_id).Distinct().ToList();

        var violations = await db.Violations.AsNoTracking()
            .Include(v => v.Penalty)
            .Where(v => (v.contract_id.HasValue && contractIds.Contains(v.contract_id.Value))
                || (v.vendor_id.HasValue && vendorIds.Contains(v.vendor_id.Value)))
            .ToListAsync(ct);

        var permits = await db.DigitalPermits.AsNoTracking()
            .Where(p => contractIds.Contains(p.contract_id))
            .ToListAsync(ct);
        var permitByContract = permits
            .GroupBy(p => p.contract_id)
            .ToDictionary(g => g.Key, g => SelectCurrentPermit(g)!);

        var permitIds = permitByContract.Values.Select(p => p.permit_id).ToList();
        var reports = await db.VendorReports.AsNoTracking()
            .Where(r => (r.vendor_id.HasValue && vendorIds.Contains(r.vendor_id.Value))
                || (r.slot_id.HasValue && slotIds.Contains(r.slot_id.Value))
                || (r.scanned_permit_id.HasValue && permitIds.Contains(r.scanned_permit_id.Value)))
            .ToListAsync(ct);

        // Same debt definition as BuildRenewalComplianceSnapshotAsync / HasOutstandingDebtAsync.
        var contractIdsWithOverdueFee = (await db.FeeScheduleItems.AsNoTracking()
            .Where(i => contractIds.Contains(i.fee_schedule.contract_id) && i.item_status == DebtStatuses.FeeItemOverdue)
            .Select(i => i.fee_schedule.contract_id)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        var result = new List<WardRenewalListItemDto>();
        foreach (var r in renewals)
        {
            var reg = r.contract.application.registration;
            var vendorName = reg.display_name;
            var currentEnd = r.contract.end_date;
            var proposedEnd = currentEnd.AddDays(r.requested_term_days);
            var pricePerDay = r.contract.slot.zone.price_per_day;
            var totalFee = r.requested_term_days * pricePerDay;

            var vList = violations.Where(v => v.contract_id == r.contract_id || v.vendor_id == r.contract.vendor_id).ToList();
            var vCount = vList.Count;
            var hasUnpaidPenalty = vList.Any(v => v.Penalty != null && IsUnpaidPenalty(v.Penalty));
            permitByContract.TryGetValue(r.contract_id, out var permit);
            var reportCount = reports.Count(report => report.vendor_id == r.contract.vendor_id
                || report.slot_id == r.contract.slot_id
                || (permit != null && report.scanned_permit_id == permit.permit_id));

            var isClean = vCount == 0
                && !hasUnpaidPenalty
                && !contractIdsWithOverdueFee.Contains(r.contract_id)
                && reportCount == 0
                && reg.registration_status == "APPROVED"
                && r.contract.contract_status == ContractStatuses.Active
                && r.contract.slot.slot_status != SlotStatuses.Suspended
                && permit?.permit_status == PermitStatuses.Active;

            var slaDueAt = r.created_at.AddDays(RenewalStatuses.DecisionSlaDays);
            var isOverdue = RenewalStatuses.Open.Contains(r.renewal_status) && Now > slaDueAt;

            result.Add(new WardRenewalListItemDto(
                Id(r.renewal_id),
                r.contract_id,
                r.contract.slot.slot_code,
                r.contract.slot.zone.zone_code ?? r.contract.slot.zone.zone_name,
                vendorName,
                r.renewal_status,
                r.requested_term_days,
                currentEnd,
                proposedEnd,
                pricePerDay,
                totalFee,
                isClean,
                vCount,
                r.created_at,
                slaDueAt,
                isOverdue));
        }

        return result;
    }

    public async Task<WardRenewalDetailDto> GetRenewalDetailAsync(
        WardActor actor,
        long renewalId,
        CancellationToken ct)
    {
        var renewal = await db.RenewalRequests.AsNoTracking()
            .Include(x => x.contract).ThenInclude(c => c.slot).ThenInclude(s => s.zone)
            .Include(x => x.contract).ThenInclude(c => c.vendor).ThenInclude(v => v.user)
            .Include(x => x.contract).ThenInclude(c => c.application).ThenInclude(a => a.registration)
            .SingleOrDefaultAsync(x => x.renewal_id == renewalId, ct);

        if (renewal is null || renewal.contract.slot.zone.ward_unit_id != actor.WardId)
        {
            throw new NotFoundException("Không tìm thấy đơn xin gia hạn tại địa bàn của bạn.");
        }

        var contract = renewal.contract;
        var reg = contract.application.registration;
        var regId = reg.registration_id;
        var regStatus = reg.registration_status;
        var vendorName = reg.display_name;
        var vendorPhone = contract.vendor.user.phone_number;
        var vendorType = reg.vendor_type;

        var today = DateOnly.FromDateTime(Now);
        var currentEndDate = contract.end_date;
        var remainingDays = currentEndDate.DayNumber - today.DayNumber;
        var proposedEndDate = currentEndDate.AddDays(renewal.requested_term_days);

        var pricePerDay = contract.slot.zone.price_per_day;
        var totalFee = renewal.requested_term_days * pricePerDay;

        var compliance = await BuildRenewalComplianceSnapshotAsync(renewal, reg, ct);
        var canApprove = (renewal.renewal_status is RenewalStatuses.Pending or RenewalStatuses.UnderReview)
            && compliance.Blockers.Count == 0;
        var isFastTrack = canApprove && compliance.Scorecard.IsCleanRecord;

        var slaDueAt = renewal.created_at.AddDays(RenewalStatuses.DecisionSlaDays);
        var isOverdue = RenewalStatuses.Open.Contains(renewal.renewal_status) && Now > slaDueAt;

        return new WardRenewalDetailDto(
            Id(renewal.renewal_id),
            contract.contract_id,
            renewal.renewal_status,
            renewal.requested_term_days,
            currentEndDate,
            proposedEndDate,
            remainingDays,
            contract.slot_id,
            contract.slot.slot_code,
            contract.slot.zone.zone_code ?? contract.slot.zone.zone_name,
            contract.slot.width_meters ?? 0,
            contract.slot.length_meters ?? 0,
            pricePerDay,
            totalFee,
            contract.vendor_id,
            vendorName,
            vendorPhone,
            vendorType,
            regId,
            regStatus,
            renewal.review_decision_reason,
            renewal.reviewed_by?.ToString(CultureInfo.InvariantCulture),
            renewal.reviewed_at,
            canApprove,
            isFastTrack,
            compliance.Blockers,
            compliance.Scorecard,
            renewal.created_at,
            slaDueAt,
            isOverdue);
    }

    public async Task<WardRenewalDetailDto> DecideRenewalAsync(
        WardActor actor,
        long renewalId,
        WardRenewalDecision decision,
        CancellationToken ct)
    {
        await Write(async () =>
        {
            var renewal = await db.RenewalRequests
                .Include(x => x.contract).ThenInclude(c => c.slot).ThenInclude(s => s.zone)
                .Include(x => x.contract).ThenInclude(c => c.vendor)
                .Include(x => x.contract).ThenInclude(c => c.application).ThenInclude(a => a.registration)
                .SingleOrDefaultAsync(x => x.renewal_id == renewalId, ct);

            if (renewal is null || renewal.contract.slot.zone.ward_unit_id != actor.WardId)
            {
                throw new NotFoundException("Không tìm thấy đơn xin gia hạn tại địa bàn của bạn.");
            }

            if (!string.Equals(renewal.renewal_status, decision.ExpectedStatus, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConflictException("Đơn đã được xử lý hoặc thay đổi trạng thái trước đó. Vui lòng tải lại.");
            }

            if (renewal.renewal_status is not (RenewalStatuses.Pending or RenewalStatuses.UnderReview))
            {
                throw new ConflictException("Đơn gia hạn đã có quyết định cuối cùng. Vui lòng tải lại hồ sơ.");
            }

            var isApprove = string.Equals(decision.Decision, "APPROVE", StringComparison.OrdinalIgnoreCase);
            var reason = decision.Reason.Trim();

            if (isApprove)
            {
                var compliance = await BuildRenewalComplianceSnapshotAsync(
                    renewal,
                    renewal.contract.application.registration,
                    ct);
                if (compliance.Blockers.Count > 0)
                {
                    throw new DomainRuleException(string.Join(" ", compliance.Blockers));
                }

                var currentEndDate = renewal.contract.end_date;
                var newEndDate = currentEndDate.AddDays(renewal.requested_term_days);

                renewal.renewal_status = "APPROVED";
                renewal.new_end_date = newEndDate;
                renewal.reviewed_by = actor.UserId;
                renewal.reviewer_role = RoleCodes.WardAuthority;
                renewal.reviewed_at = Now;
                renewal.review_decision_reason = reason;

                renewal.contract.end_date = newEndDate;

                // Mark current active fee schedule superseded
                var feeSchedules = await db.FeeSchedules
                    .Where(fs => fs.contract_id == renewal.contract_id)
                    .ToListAsync(ct);

                var activeFee = feeSchedules.FirstOrDefault(fs => fs.superseded_at == null);
                if (activeFee != null)
                {
                    activeFee.superseded_at = Now;
                }

                var nextRev = feeSchedules.Count > 0 ? feeSchedules.Max(fs => fs.revision) + 1 : 1;
                var pricePerDay = renewal.contract.slot.zone.price_per_day;
                var totalFee = renewal.requested_term_days * pricePerDay;

                db.FeeSchedules.Add(new FeeSchedule
                {
                    contract_id = renewal.contract_id,
                    revision = nextRev,
                    total_amount = totalFee,
                    generated_at = Now
                });

                db.AuditLogs.Add(new AuditLog
                {
                    actor_user_id = actor.UserId,
                    action = "RENEWAL_APPROVED",
                    entity_type = "RenewalRequest",
                    entity_id = renewal.renewal_id,
                    details = $"Cán bộ {actor.Name} phê duyệt gia hạn hợp đồng #{renewal.contract_id} tại ô {renewal.contract.slot.slot_code} thêm {renewal.requested_term_days} ngày đến {newEndDate:yyyy-MM-dd}. Lý do: {reason}",
                    created_at = Now
                });

                db.Notifications.Add(new Notification
                {
                    user_id = renewal.contract.vendor.user_id,
                    notification_type = "RENEWAL_APPROVED",
                    title = "Đã được phê duyệt gia hạn hợp đồng hè phố",
                    body = $"UBND Phường đã phê duyệt gia hạn hợp đồng thuê hè phố ô {renewal.contract.slot.slot_code} thêm {renewal.requested_term_days} ngày đến hết ngày {newEndDate:dd/MM/yyyy}. Giấy phép số QR đã tự động cập nhật thời hạn mới.",
                    related_entity_type = "RenewalRequest",
                    related_entity_id = renewal.renewal_id,
                    is_read = false,
                    sent_at = Now
                });
            }
            else
            {
                renewal.renewal_status = "REJECTED";
                renewal.reviewed_by = actor.UserId;
                renewal.reviewer_role = RoleCodes.WardAuthority;
                renewal.reviewed_at = Now;
                renewal.review_decision_reason = reason;

                db.AuditLogs.Add(new AuditLog
                {
                    actor_user_id = actor.UserId,
                    action = "RENEWAL_REJECTED",
                    entity_type = "RenewalRequest",
                    entity_id = renewal.renewal_id,
                    details = $"Cán bộ {actor.Name} từ chối gia hạn hợp đồng #{renewal.contract_id} tại ô {renewal.contract.slot.slot_code}. Lý do: {reason}",
                    created_at = Now
                });

                db.Notifications.Add(new Notification
                {
                    user_id = renewal.contract.vendor.user_id,
                    notification_type = "RENEWAL_REJECTED",
                    title = "Yêu cầu gia hạn hợp đồng hè phố bị từ chối",
                    body = $"UBND Phường đã từ chối yêu cầu gia hạn hợp đồng ô {renewal.contract.slot.slot_code}. Lý do: {reason}",
                    related_entity_type = "RenewalRequest",
                    related_entity_id = renewal.renewal_id,
                    is_read = false,
                    sent_at = Now
                });
            }
        }, ct);

        return await GetRenewalDetailAsync(actor, renewalId, ct);
    }

    /// <summary>
    /// Idea 4 (WARD-09): batch fast-approval for the officer's Fast-track filtered queue.
    /// Each item runs through the same DecideRenewalAsync as a single-item decision -- its own
    /// Serializable transaction, its own BR-16/contract-status checks, its own AuditLog/
    /// Notification -- so a batch call is exactly "click Approve N times" with one shared
    /// reason, never a shortcut that skips any check a single approval would run. One item
    /// failing (stale status, newly-ineligible vendor, etc.) never aborts the rest.
    /// </summary>
    public async Task<WardRenewalBatchDecisionResult> BatchDecideRenewalsAsync(
        WardActor actor,
        WardRenewalBatchDecisionRequest request,
        CancellationToken ct)
    {
        var isApprove = string.Equals(request.Decision, "APPROVE", StringComparison.OrdinalIgnoreCase);
        var results = new List<WardRenewalBatchDecisionItemResult>();

        foreach (var item in request.Items)
        {
            try
            {
                var detail = await DecideRenewalAsync(
                    actor,
                    item.RenewalId,
                    new WardRenewalDecision(request.Decision, request.Reason, item.ExpectedStatus),
                    ct);

                results.Add(new WardRenewalBatchDecisionItemResult(
                    item.RenewalId,
                    Success: true,
                    NewEndDate: isApprove ? detail.CurrentEndDate : null));
            }
            catch (Exception ex) when (ex is NotFoundException or ConflictException or DomainRuleException)
            {
                results.Add(new WardRenewalBatchDecisionItemResult(
                    item.RenewalId, Success: false, ErrorMessage: ex.Message));
            }
        }

        return new WardRenewalBatchDecisionResult(
            TotalRequested: request.Items.Count,
            SuccessCount: results.Count(r => r.Success),
            FailureCount: results.Count(r => !r.Success),
            Results: results);
    }
    #endregion

    #region On-site Inspection & Permit Actions
    public async Task<InspectWardPermitResult> InspectPermitAsync(
        WardActor actor,
        InspectWardPermitRequest request,
        CancellationToken ct)
    {
        var raw = request.PermitCodeOrPayload?.Trim() ?? "";

        long? targetContractId = null;
        if (permitTokens.TryParse(raw, out var claims))
        {
            targetContractId = claims.ContractId;
        }

        var viewQuery = db.vw_PermitValidities.AsNoTracking();
        vw_PermitValidity? validity = null;

        if (targetContractId.HasValue)
        {
            validity = await viewQuery.FirstOrDefaultAsync(x => x.contract_id == targetContractId.Value, ct);
        }

        validity ??= await viewQuery.FirstOrDefaultAsync(x => x.qr_payload == raw, ct);

        if (validity is null && long.TryParse(raw, out var parsedId))
        {
            validity = await viewQuery.FirstOrDefaultAsync(x => x.permit_id == parsedId || x.contract_id == parsedId, ct);
        }

        if (validity is null)
        {
            return new InspectWardPermitResult(
                Found: false, IsValid: false, EffectiveStatus: "NOT_FOUND",
                PermitId: null, ContractId: null, VendorId: null, VendorName: null,
                SlotId: null, SlotCode: null, SlotStreet: null, Width: null, Length: null,
                StartDate: null, EndDate: null, SlotLatitude: null, SlotLongitude: null,
                DistanceMeters: null, IsLocationMatched: false,
                LocationWarning: "Không tìm thấy giấy phép hợp lệ trên hệ thống.",
                AiVisionResult: null);
        }

        var contract = await db.RentalContracts.AsNoTracking()
            .Include(c => c.slot).ThenInclude(s => s.zone)
            .Include(c => c.vendor).ThenInclude(v => v.BusinessRegistrations)
            .SingleOrDefaultAsync(c => c.contract_id == validity.contract_id, ct);

        var slot = contract?.slot;
        var vendorName = contract?.vendor.BusinessRegistrations.FirstOrDefault()?.display_name
            ?? $"Hộ kinh doanh #{validity.vendor_id}";

        // effective_status (vw_PermitValidity) can only ever be VALID/NOT_YET_VALID/SUSPENDED/
        // EXPIRED/REVOKED -- it is never "ACTIVE" (that's the raw DigitalPermits.permit_status
        // column instead). Comparing against "ACTIVE" here made every scanned permit, including
        // genuinely valid ones, always report as invalid.
        var isPermitValid = IsPermitEffectivelyValid(validity.effective_status);

        double? distanceMeters = null;
        var isLocationMatched = true;
        string? locationWarning = null;

        if (request.Latitude.HasValue && request.Longitude.HasValue && slot != null)
        {
            distanceMeters = CalculateDistanceMeters(
                request.Latitude.Value, request.Longitude.Value,
                (double)slot.latitude, (double)slot.longitude);

            // Urban GPS tolerance: 25 meters. This is a deterministic distance
            // calculation, not an AI judgement -- no [AI] label.
            if (distanceMeters > 25.0)
            {
                isLocationMatched = false;
                locationWarning = $"Cảnh báo lệch vị trí: điểm quét thực tế cách ô cấp phép {Math.Round(distanceMeters.Value, 1)}m (vượt dung sai 25m).";
            }
        }

        AiEncroachmentResult? aiVision = null;
        if (!string.IsNullOrWhiteSpace(request.InspectionPhotoUrl))
        {
            aiVision = await aiService.AnalyzeInspectionPhotoAsync(
                request.InspectionPhotoUrl,
                slot?.width_meters.HasValue == true ? (double)slot.width_meters.Value : null,
                slot?.length_meters.HasValue == true ? (double)slot.length_meters.Value : null,
                ct);
        }

        db.PermitScanLogs.Add(new PermitScanLog
        {
            permit_id = validity.permit_id,
            qr_payload = raw,
            scanned_by = actor.UserId,
            scan_context = "WARD_INSPECTION",
            scanner_role = RoleCodes.WardAuthority,
            scan_result = validity.effective_status ?? "UNKNOWN",
            photo_url = request.InspectionPhotoUrl,
            latitude = request.Latitude.HasValue ? (decimal)request.Latitude.Value : null,
            longitude = request.Longitude.HasValue ? (decimal)request.Longitude.Value : null,
            scanned_at = Now
        });
        await db.SaveChangesAsync(ct);

        return new InspectWardPermitResult(
            Found: true,
            IsValid: isPermitValid,
            EffectiveStatus: validity.effective_status ?? "UNKNOWN",
            PermitId: validity.permit_id,
            ContractId: validity.contract_id,
            VendorId: validity.vendor_id,
            VendorName: vendorName,
            SlotId: slot?.slot_id,
            SlotCode: slot?.slot_code,
            SlotStreet: slot?.zone.zone_code ?? slot?.zone.zone_name,
            Width: slot?.width_meters,
            Length: slot?.length_meters,
            StartDate: validity.start_date,
            EndDate: validity.end_date,
            SlotLatitude: slot != null ? (double)slot.latitude : null,
            SlotLongitude: slot != null ? (double)slot.longitude : null,
            DistanceMeters: distanceMeters.HasValue ? Math.Round(distanceMeters.Value, 1) : null,
            IsLocationMatched: isLocationMatched,
            LocationWarning: locationWarning,
            AiVisionResult: aiVision);
    }

    public async Task<bool> ExecutePermitActionAsync(
        WardActor actor,
        long permitId,
        WardPermitActionRequest request,
        CancellationToken ct)
    {
        var permit = await db.DigitalPermits
            .Include(p => p.contract).ThenInclude(c => c.slot).ThenInclude(s => s.zone)
            .SingleOrDefaultAsync(p => p.permit_id == permitId, ct);

        if (permit is null || permit.contract.slot.zone.ward_unit_id != actor.WardId)
        {
            throw new NotFoundException("Không tìm thấy giấy phép tại địa bàn phường của bạn.");
        }

        var isSuspend = string.Equals(request.Action, "SUSPEND", StringComparison.OrdinalIgnoreCase);
        var newStatus = isSuspend ? PermitStatuses.Suspended : PermitStatuses.Revoked;

        permit.permit_status = newStatus;
        if (isSuspend)
        {
            permit.suspended_at = Now;
            permit.suspension_reason = request.Reason.Trim();
            // Contract stays legally in force but under suspension -- the slot remains
            // occupied (not returned to AVAILABLE), since this isn't a termination.
            permit.contract.contract_status = ContractStatuses.Suspended;
        }
        else
        {
            permit.revoked_at = Now;
            permit.revocation_reason = request.Reason.Trim();
            // A prior version only ever wrote DigitalPermit.permit_status here, leaving
            // RentalContract and SidewalkSlot permanently out of sync with a revoked permit --
            // the slot would never be freed and the contract would stay ACTIVE forever.
            permit.contract.contract_status = ContractStatuses.Revoked;

            var stillOccupied = await db.RentalContracts.AsNoTracking()
                .AnyAsync(c => c.slot_id == permit.contract.slot_id
                    && c.contract_id != permit.contract.contract_id
                    && (c.contract_status == ContractStatuses.Active || c.contract_status == ContractStatuses.Suspended)
                    && c.end_date >= DateOnly.FromDateTime(Now), ct);

            if (!stillOccupied)
            {
                permit.contract.slot.slot_status = SlotStatuses.Available;
            }
        }

        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actor.UserId,
            action = $"PERMIT_{newStatus}",
            entity_type = "DigitalPermit",
            entity_id = permit.permit_id,
            details = $"Cán bộ {actor.Name} quyết định {newStatus} giấy phép. Lý do: {request.Reason.Trim()}",
            created_at = Now
        });

        db.Notifications.Add(new Notification
        {
            user_id = permit.contract.vendor_id,
            notification_type = "PERMIT_STATUS_SUSPENDED",
            title = isSuspend ? "Giấy phép sử dụng hè phố bị tạm đình chỉ" : "Giấy phép sử dụng hè phố đã bị thu hồi",
            body = $"UBND Phường đã {(isSuspend ? "tạm đình chỉ" : "thu hồi")} giấy phép tại ô {permit.contract.slot.slot_code}. Lý do: {request.Reason.Trim()}",
            related_entity_type = "DigitalPermit",
            related_entity_id = permit.permit_id,
            is_read = false,
            sent_at = Now
        });

        await db.SaveChangesAsync(ct);
        return true;
    }
    #endregion

    #region Violations & Sanctions (2-Step Administrative Flow)
    public async Task<IReadOnlyList<PenaltyScheduleItemDto>> ListPenaltySchedulesAsync(
        WardActor actor,
        CancellationToken ct)
    {
        var list = await db.PenaltyFeeSchedules.AsNoTracking()
            .Include(s => s.violation_typeNavigation)
            .Where(s => s.ward_unit_id == actor.WardId && s.effective_to == null)
            .OrderBy(s => s.penalty_schedule_id)
            .Select(s => new PenaltyScheduleItemDto(
                s.penalty_schedule_id,
                s.violation_type,
                s.violation_typeNavigation.description,
                s.penalty_amount,
                s.legal_basis))
            .ToListAsync(ct);

        return list;
    }

    public async Task<IReadOnlyList<WardViolationListItemDto>> ListViolationsAsync(
        WardActor actor,
        string? status,
        int page,
        CancellationToken ct)
    {
        var query = db.Violations.AsNoTracking()
            .Include(v => v.Penalty)
            .Include(v => v.violation_typeNavigation)
            .Include(v => v.slot).ThenInclude(s => s!.zone)
            .Include(v => v.vendor).ThenInclude(vnd => vnd!.BusinessRegistrations)
            .Include(v => v.UserAccount)
            .Where(v => v.slot == null || v.slot.zone.ward_unit_id == actor.WardId);

        var offset = (page - 1) * 20;
        var items = await query
            .OrderByDescending(v => v.recorded_at)
            .Skip(offset)
            .Take(20)
            .Select(v => new WardViolationListItemDto(
                v.violation_id,
                v.contract_id,
                v.slot != null ? v.slot.slot_code : "N/A",
                v.vendor != null && v.vendor.BusinessRegistrations.Any()
                    ? v.vendor.BusinessRegistrations.First().display_name
                    : "Hộ kinh doanh",
                v.violation_type,
                v.violation_typeNavigation.description,
                v.Penalty != null ? v.Penalty.penalty_status : "PENDING_SANCTION",
                v.Penalty != null ? v.Penalty.amount : null,
                v.recorded_at,
                v.UserAccount != null ? v.UserAccount.full_name ?? "Cán bộ" : "Cán bộ tuần tra"))
            .ToListAsync(ct);

        return items;
    }

    public async Task<WardViolationDetailDto> GetViolationDetailAsync(
        WardActor actor,
        long violationId,
        CancellationToken ct)
    {
        var v = await db.Violations.AsNoTracking()
            .Include(x => x.Penalty)
            .Include(x => x.violation_typeNavigation)
            .Include(x => x.slot).ThenInclude(s => s!.zone)
            .Include(x => x.vendor).ThenInclude(vnd => vnd!.BusinessRegistrations)
            .Include(x => x.UserAccount)
            .SingleOrDefaultAsync(x => x.violation_id == violationId, ct);

        if (v is null)
        {
            throw new NotFoundException("Không tìm thấy biên bản vi phạm.");
        }

        var vendorName = v.vendor?.BusinessRegistrations.FirstOrDefault()?.display_name ?? "Hộ kinh doanh";
        var recordedByName = v.UserAccount?.full_name ?? "Cán bộ tuần tra";

        // Rule-based repeat-offender count -- not an LLM call, just a count query.
        // Gives the officer context ("3rd violation in 90 days") for free.
        var since = Now.AddDays(-90);
        var recentCount = v.vendor_id.HasValue
            ? await db.Violations.AsNoTracking()
                .CountAsync(x => x.vendor_id == v.vendor_id.Value && x.recorded_at >= since, ct)
            : 0;

        // AI Legal Co-pilot: classify + draft FROM the ward's own configured
        // schedule -- never invents a legal citation or amount [BR-41/42].
        var availableSchedules = await ListPenaltySchedulesAsync(actor, ct);
        var aiSuggestion = await aiService.ClassifyAndDraftAsync(v.description, availableSchedules, ct);

        var status = v.Penalty?.penalty_status ?? "PENDING_SANCTION";

        return new WardViolationDetailDto(
            v.violation_id,
            v.contract_id,
            v.slot_id,
            v.slot?.slot_code,
            v.vendor_id,
            vendorName,
            v.violation_type,
            v.violation_typeNavigation?.description ?? v.violation_type,
            v.description,
            v.evidence_url,
            v.recorded_at,
            recordedByName,
            status,
            v.Penalty?.amount,
            v.Penalty?.decision_number,
            v.Penalty?.signer_name,
            v.Penalty?.signer_title,
            v.Penalty?.created_at,
            recentCount,
            aiSuggestion);
    }

    public async Task<WardViolationDetailDto> RecordViolationAsync(
        WardActor actor,
        RecordWardViolationRequest request,
        CancellationToken ct)
    {
        var violation = new Violation
        {
            contract_id = request.ContractId,
            slot_id = request.SlotId,
            vendor_id = request.VendorId,
            violation_type = request.ViolationType,
            description = request.Description.Trim(),
            evidence_url = request.EvidenceUrl,
            recorded_by = actor.UserId,
            recorder_role = RoleCodes.WardAuthority,
            source = "OFFICER",
            recorded_at = Now
        };
        db.Violations.Add(violation);

        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actor.UserId,
            action = "VIOLATION_RECORDED",
            entity_type = "Violation",
            entity_id = violation.violation_id,
            details = $"Cán bộ {actor.Name} lập biên bản vi phạm hành vi {request.ViolationType}: {request.Description.Trim()}",
            created_at = Now
        });

        await db.SaveChangesAsync(ct);
        return await GetViolationDetailAsync(actor, violation.violation_id, ct);
    }

    public async Task<WardViolationDetailDto> SanctionViolationAsync(
        WardActor actor,
        long violationId,
        SanctionWardViolationRequest request,
        CancellationToken ct)
    {
        var violation = await db.Violations
            .Include(v => v.Penalty)
            .SingleOrDefaultAsync(v => v.violation_id == violationId, ct);

        if (violation is null)
        {
            throw new NotFoundException("Không tìm thấy biên bản vi phạm.");
        }

        // BR-33: amount always comes from the ward's configured schedule row --
        // never from any AI-suggested figure, even if the two happen to differ.
        var schedule = await db.PenaltyFeeSchedules.AsNoTracking()
            .SingleOrDefaultAsync(s => s.penalty_schedule_id == request.PenaltyScheduleId, ct);

        if (schedule is null || schedule.ward_unit_id != actor.WardId)
        {
            throw new NotFoundException("Không tìm thấy khung xử phạt đã chọn tại địa bàn phường của bạn.");
        }

        // WARD-12/13 authority split: the patrolling officer who recorded the violation
        // does not have sanction authority under the Law on Handling of Administrative
        // Violations -- only the Chairman/Vice-Chairman (or a written delegate) does.
        // Self-declared for now (see Contracts.cs SanctionWardViolationRequest), not RBAC-enforced.
        var signerName = request.SignerName.Trim();
        var signerTitle = request.SignerTitle.Trim();

        if (violation.Penalty is null)
        {
            db.Penalties.Add(new Penalty
            {
                violation_id = violation.violation_id,
                penalty_schedule_id = schedule.penalty_schedule_id,
                amount = schedule.penalty_amount,
                penalty_status = "UNPAID",
                decision_number = request.DecisionNumber.Trim(),
                signer_name = signerName,
                signer_title = signerTitle,
                created_at = Now
            });
        }
        else
        {
            violation.Penalty.penalty_schedule_id = schedule.penalty_schedule_id;
            violation.Penalty.amount = schedule.penalty_amount;
            violation.Penalty.decision_number = request.DecisionNumber.Trim();
            violation.Penalty.signer_name = signerName;
            violation.Penalty.signer_title = signerTitle;
            violation.Penalty.penalty_status = "UNPAID";
        }

        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actor.UserId,
            action = "SANCTION_DECISION_ISSUED",
            entity_type = "Penalty",
            entity_id = violation.violation_id,
            details = $"{signerTitle} {signerName} ban hành Quyết định xử phạt số {request.DecisionNumber.Trim()} " +
                      $"({schedule.legal_basis ?? schedule.violation_type}). Mức phạt: {schedule.penalty_amount:N0} VND.",
            created_at = Now
        });

        if (violation.vendor_id.HasValue)
        {
            var vendorUser = await db.Vendors.AsNoTracking()
                .Where(v => v.vendor_id == violation.vendor_id.Value)
                .Select(v => v.user_id)
                .FirstOrDefaultAsync(ct);

            if (vendorUser > 0)
            {
                db.Notifications.Add(new Notification
                {
                    user_id = vendorUser,
                    notification_type = "PENALTY_SANCTION_ISSUED",
                    title = "Thông báo Quyết định xử phạt vi phạm hành chính",
                    body = $"UBND Phường đã ban hành Quyết định xử phạt số {request.DecisionNumber.Trim()}. " +
                           $"Số tiền phạt: {schedule.penalty_amount:N0} VND. Vui lòng nộp phạt theo quy định.",
                    related_entity_type = "Penalty",
                    related_entity_id = violation.violation_id,
                    is_read = false,
                    sent_at = Now
                });
            }
        }

        await db.SaveChangesAsync(ct);
        return await GetViolationDetailAsync(actor, violationId, ct);
    }
    #endregion

    #region Insights (rule-based, explainable -- no LLM)
    public async Task<IReadOnlyList<WardRiskQueueItemDto>> GetRiskQueueAsync(
        WardActor actor,
        CancellationToken ct)
    {
        var since = Now.AddDays(-90);

        var pending = await db.BusinessRegistrations.AsNoTracking()
            .Include(x => x.RegistrationEvidences)
            .Where(x => x.ward_unit_id == actor.WardId
                && (x.registration_status == "SUBMITTED" || x.registration_status == "UNDER_REVIEW"))
            .Select(x => new
            {
                x.registration_id,
                x.display_name,
                x.vendor_id,
                x.fast_track_flag,
                x.id_number,
                EvidenceCount = x.RegistrationEvidences.Count
            })
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return Array.Empty<WardRiskQueueItemDto>();
        }

        var vendorIds = pending.Select(x => x.vendor_id).Distinct().ToList();
        var recentViolationCounts = await db.Violations.AsNoTracking()
            .Where(v => v.vendor_id.HasValue && vendorIds.Contains(v.vendor_id.Value) && v.recorded_at >= since)
            .GroupBy(v => v.vendor_id!.Value)
            .Select(g => new { VendorId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.VendorId, x => x.Count, ct);

        var result = new List<WardRiskQueueItemDto>();
        foreach (var reg in pending)
        {
            var breakdown = new List<RiskScoreBreakdownItem>();

            var violationCount = recentViolationCounts.GetValueOrDefault(reg.vendor_id);
            if (violationCount > 0)
            {
                var points = Math.Min(violationCount * 3, 9);
                breakdown.Add(new RiskScoreBreakdownItem(
                    $"{violationCount} vi phạm trong 90 ngày qua", points));
            }

            if (reg.fast_track_flag && string.IsNullOrWhiteSpace(reg.id_number))
            {
                breakdown.Add(new RiskScoreBreakdownItem(
                    "Đánh dấu xét nhanh nhưng chưa xác minh được CCCD qua AI-OCR", 5));
            }

            if (reg.EvidenceCount == 0)
            {
                breakdown.Add(new RiskScoreBreakdownItem(
                    "Chưa có tài liệu minh chứng nào đính kèm", 4));
            }

            if (breakdown.Count > 0)
            {
                result.Add(new WardRiskQueueItemDto(
                    Id(reg.registration_id), reg.display_name,
                    breakdown.Sum(b => b.Points), breakdown));
            }
        }

        return result.OrderByDescending(x => x.Score).ToList();
    }

    public async Task<IReadOnlyList<WardPatrolHeatmapPointDto>> GetPatrolHeatmapAsync(
        WardActor actor,
        CancellationToken ct)
    {
        // Pulled to memory deliberately: municipal pilot scale (hundreds, not
        // millions, of rows per ward) and DayOfWeek/Hour grouping is simpler
        // and more portable done client-side than relying on SQL Server date-part
        // translation.
        var rows = await db.Violations.AsNoTracking()
            .Where(v => v.slot != null && v.slot.zone.ward_unit_id == actor.WardId)
            .Select(v => new { v.recorded_at, ZoneId = v.slot!.zone_id, ZoneName = v.slot.zone.zone_code ?? v.slot.zone.zone_name })
            .ToListAsync(ct);

        return rows
            .GroupBy(x => new { x.ZoneId, x.ZoneName, DayOfWeek = (int)x.recorded_at.DayOfWeek, Hour = x.recorded_at.Hour })
            .Select(g => new WardPatrolHeatmapPointDto(g.Key.ZoneId, g.Key.ZoneName, g.Key.DayOfWeek, g.Key.Hour, g.Count()))
            .OrderByDescending(x => x.ViolationCount)
            .ToList();
    }
    #endregion

    #region Helpers
    /// <summary>
    /// effective_status (vw_PermitValidity) can only ever be VALID/NOT_YET_VALID/SUSPENDED/
    /// EXPIRED/REVOKED -- it is never "ACTIVE" (that's the raw DigitalPermits.permit_status
    /// column instead). Public/static so it's directly unit-testable without a database
    /// (vw_PermitValidity is a real SQL Server view with no SQLite equivalent in tests).
    /// </summary>
    public static bool IsPermitEffectivelyValid(string? effectiveStatus) =>
        string.Equals(effectiveStatus, PermitEffectiveStatuses.Valid, StringComparison.OrdinalIgnoreCase);

    private static double CalculateDistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000.0;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
    #endregion
}
