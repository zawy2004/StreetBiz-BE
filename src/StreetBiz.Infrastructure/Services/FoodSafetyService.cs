using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.FoodSafety;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Services;

/// <summary>
/// ATTP certificates: the vendor submits dishes plus evidence, the ward reviews and forwards
/// the file to the department (outside the system), then records the department's result.
/// Every transition runs in a serializable transaction and checks the status the reviewer saw.
/// </summary>
public sealed class FoodSafetyService(
    StreetBizDbContext db,
    IVendorContext vendors,
    IWardActorContext wardActors,
    IFileStorage storage,
    TimeProvider clock) : IFoodSafetyService
{
    private const int MaxEvidence = 10;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(Now.AddHours(7));

    public async Task<IReadOnlyList<FoodSafetyApplicationDto>> VendorList(CancellationToken ct)
    {
        var vendor = await vendors.RequireVendorIdAsync(ct);
        var ids = await db.FoodSafetyApplications.AsNoTracking().Where(x => x.vendor_id == vendor)
            .OrderByDescending(x => x.submitted_at).ThenByDescending(x => x.application_id)
            .Select(x => x.application_id).ToListAsync(ct);
        return await Load(ids, forWard: false, ct);
    }

    public async Task<FoodSafetyApplicationDto> VendorGet(long id, CancellationToken ct)
    {
        var vendor = await vendors.RequireVendorIdAsync(ct);
        if (!await db.FoodSafetyApplications.AnyAsync(x => x.application_id == id && x.vendor_id == vendor, ct))
            throw new NotFoundException("Không tìm thấy hồ sơ ATTP của bạn.");
        return (await Load([id], forWard: false, ct)).Single();
    }

    public async Task<FoodSafetyApplicationDto> Submit(FoodSafetySubmitInput input, CancellationToken ct)
    {
        var vendor = await vendors.RequireVendorIdAsync(ct);
        await ValidateInput(vendor, input, ct);
        long id = 0;
        await Write(async () =>
        {
            await CheckDishes(vendor, input, exceptApplicationId: null, ct);
            var row = new FoodSafetyApplication
            {
                storefront_id = input.StorefrontId,
                vendor_id = vendor,
                application_status = FoodSafetyStatuses.Submitted,
                created_at = Now,
            };
            Fill(row, input);
            db.FoodSafetyApplications.Add(row);
            await db.SaveChangesAsync(ct);
            id = row.application_id;
        }, ct);
        return await VendorGet(id, ct);
    }

    public async Task<FoodSafetyApplicationDto> Resubmit(long id, FoodSafetySubmitInput input, CancellationToken ct)
    {
        var vendor = await vendors.RequireVendorIdAsync(ct);
        await ValidateInput(vendor, input, ct);
        await Write(async () =>
        {
            var row = await db.FoodSafetyApplications.Include(x => x.FoodSafetyApplicationItems).Include(x => x.FoodSafetyEvidences)
                .SingleOrDefaultAsync(x => x.application_id == id && x.vendor_id == vendor, ct)
                ?? throw new NotFoundException("Không tìm thấy hồ sơ ATTP của bạn.");
            if (row.application_status != FoodSafetyStatuses.MoreInformationRequired)
                throw new ConflictException("Chỉ bổ sung hồ sơ khi phường yêu cầu bổ sung.");
            if (row.storefront_id != input.StorefrontId)
                throw new ConflictException("Không thể đổi gian hàng của hồ sơ.");
            await CheckDishes(vendor, input, exceptApplicationId: id, ct);
            db.FoodSafetyApplicationItems.RemoveRange(row.FoodSafetyApplicationItems);
            db.FoodSafetyEvidences.RemoveRange(row.FoodSafetyEvidences);
            row.application_status = FoodSafetyStatuses.Submitted;
            Fill(row, input);
            await db.SaveChangesAsync(ct);
        }, ct);
        return await VendorGet(id, ct);
    }

    public async Task<FoodSafetyApplicationDto> Withdraw(long id, CancellationToken ct)
    {
        var vendor = await vendors.RequireVendorIdAsync(ct);
        await Write(async () =>
        {
            var row = await db.FoodSafetyApplications.SingleOrDefaultAsync(x => x.application_id == id && x.vendor_id == vendor, ct)
                ?? throw new NotFoundException("Không tìm thấy hồ sơ ATTP của bạn.");
            if (row.application_status is not (FoodSafetyStatuses.Submitted or FoodSafetyStatuses.MoreInformationRequired))
                throw new ConflictException("Hồ sơ đã được chuyển cục hoặc đã có kết quả; không thể rút.");
            row.application_status = FoodSafetyStatuses.Withdrawn;
            row.updated_at = Now;
            await db.SaveChangesAsync(ct);
        }, ct);
        return await VendorGet(id, ct);
    }

    public async Task<IReadOnlyList<FoodSafetyApplicationDto>> WardList(string? status, CancellationToken ct)
    {
        var actor = await wardActors.RequireAsync(ct);
        var query = InWard(actor.WardId);
        query = string.IsNullOrWhiteSpace(status)
            ? query.Where(x => x.application_status != FoodSafetyStatuses.Withdrawn)
            : query.Where(x => x.application_status == status);
        var ids = await query
            // Work first: files waiting on the ward, then those waiting on the department.
            .OrderBy(x => x.application_status == FoodSafetyStatuses.Submitted ? 0
                : x.application_status == FoodSafetyStatuses.Forwarded ? 1 : 2)
            .ThenByDescending(x => x.submitted_at).ThenByDescending(x => x.application_id)
            .Select(x => x.application_id).Take(100).ToListAsync(ct);
        return await Load(ids, forWard: true, ct);
    }

    public async Task<FoodSafetyApplicationDto> WardGet(long id, CancellationToken ct)
    {
        var actor = await wardActors.RequireAsync(ct);
        if (!await InWard(actor.WardId).AnyAsync(x => x.application_id == id, ct))
            throw new NotFoundException("Không tìm thấy hồ sơ ATTP trong phường của bạn.");
        return (await Load([id], forWard: true, ct)).Single();
    }

    public async Task<FoodSafetyApplicationDto> Decide(long id, FoodSafetyDecisionInput input, CancellationToken ct)
    {
        var actor = await wardActors.RequireAsync(ct);
        var reason = input.Reason?.Trim() ?? "";
        if (reason.Length is 0 or > 500) throw new DomainRuleException("Nhập lý do / ghi chú từ 1 đến 500 ký tự.");
        await Write(async () =>
        {
            var row = await InWard(actor.WardId).SingleOrDefaultAsync(x => x.application_id == id, ct)
                ?? throw new NotFoundException("Không tìm thấy hồ sơ ATTP trong phường của bạn.");
            if (row.application_status != input.ExpectedStatus || !WardActions(row.application_status).Contains(input.Decision))
                throw new ConflictException("Hồ sơ đã thay đổi hoặc không còn cho phép thao tác này. Tải lại để kiểm tra.");
            var previous = row.application_status;
            string title;
            switch (input.Decision)
            {
                case FoodSafetyDecisions.RequestInfo:
                    Review(row, actor, reason, FoodSafetyStatuses.MoreInformationRequired);
                    title = "Hồ sơ ATTP cần bổ sung";
                    break;
                case FoodSafetyDecisions.Reject:
                    Review(row, actor, reason, FoodSafetyStatuses.Rejected);
                    title = "Hồ sơ ATTP bị từ chối";
                    break;
                case FoodSafetyDecisions.Forward:
                    var department = input.DepartmentName?.Trim() ?? "";
                    if (department.Length is 0 or > 200) throw new DomainRuleException("Nhập tên cơ quan kiểm tra ATTP (tối đa 200 ký tự).");
                    Review(row, actor, reason, FoodSafetyStatuses.Forwarded);
                    row.forwarded_at = Now;
                    row.department_name = department;
                    title = "Hồ sơ ATTP đã chuyển cơ quan kiểm tra";
                    break;
                case FoodSafetyDecisions.RecordApproved:
                    var number = input.CertificateNumber?.Trim() ?? "";
                    if (number.Length is 0 or > 60) throw new DomainRuleException("Nhập số giấy chứng nhận ATTP (tối đa 60 ký tự).");
                    if (input.IssuedOn is not { } issued || input.ExpiresOn is not { } expires)
                        throw new DomainRuleException("Nhập ngày cấp và ngày hết hạn của giấy ATTP.");
                    if (issued > Today) throw new DomainRuleException("Ngày cấp không được sau hôm nay.");
                    if (expires <= Today || expires <= issued) throw new DomainRuleException("Ngày hết hạn phải sau hôm nay và sau ngày cấp.");
                    Record(row, actor, reason, FoodSafetyStatuses.Approved);
                    row.certificate_number = number;
                    row.issued_on = issued;
                    row.expires_on = expires;
                    title = "Món ăn đã đạt ATTP";
                    break;
                default:
                    Record(row, actor, reason, FoodSafetyStatuses.Rejected);
                    title = "Món ăn không đạt ATTP";
                    break;
            }
            await Notify(row, title, reason, ct);
            db.AuditLogs.Add(new AuditLog
            {
                actor_user_id = actor.UserId,
                action = $"WARD_FOOD_SAFETY_{input.Decision}",
                entity_type = "food-safety",
                entity_id = id,
                details = JsonSerializer.Serialize(new { PreviousStatus = previous, Reason = reason }),
                created_at = Now,
            });
            await db.SaveChangesAsync(ct);
        }, ct);
        return (await Load([id], forWard: true, ct)).Single();
    }

    /// <summary>Lets a ward officer read an evidence file attached to an ATTP file in their ward.</summary>
    public Task<bool> EvidenceBelongsToWardAsync(string fileUrl, int wardId, CancellationToken ct) =>
        db.FoodSafetyEvidences.AnyAsync(x => x.file_url == fileUrl &&
            x.application.storefront.registration.ward_unit_id == wardId, ct);

    private IQueryable<FoodSafetyApplication> InWard(int wardId) =>
        db.FoodSafetyApplications.Where(x => x.storefront.registration.ward_unit_id == wardId);

    private static string[] WardActions(string status) => status switch
    {
        FoodSafetyStatuses.Submitted => [FoodSafetyDecisions.Forward, FoodSafetyDecisions.RequestInfo, FoodSafetyDecisions.Reject],
        FoodSafetyStatuses.Forwarded => [FoodSafetyDecisions.RecordApproved, FoodSafetyDecisions.RecordRejected],
        _ => [],
    };

    private static string[] VendorActions(string status) => status switch
    {
        FoodSafetyStatuses.Submitted => ["WITHDRAW"],
        FoodSafetyStatuses.MoreInformationRequired => ["RESUBMIT", "WITHDRAW"],
        _ => [],
    };

    private void Review(FoodSafetyApplication row, WardActor actor, string reason, string status)
    {
        row.application_status = status;
        row.reviewed_by = actor.UserId;
        row.review_reason = reason;
        row.reviewed_at = Now;
        row.updated_at = Now;
    }

    private void Record(FoodSafetyApplication row, WardActor actor, string reason, string status)
    {
        row.application_status = status;
        row.result_reason = reason;
        row.result_recorded_by = actor.UserId;
        row.result_recorded_at = Now;
        row.updated_at = Now;
    }

    private void Fill(FoodSafetyApplication row, FoodSafetySubmitInput input)
    {
        row.vendor_note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        row.submitted_at = Now;
        row.updated_at = Now;
        row.reviewed_by = null;
        row.review_reason = null;
        row.reviewed_at = null;
        foreach (var itemId in input.MenuItemIds.Distinct())
            row.FoodSafetyApplicationItems.Add(new FoodSafetyApplicationItem { menu_item_id = itemId });
        foreach (var evidence in input.Evidence)
            row.FoodSafetyEvidences.Add(new FoodSafetyEvidence
            {
                evidence_type = evidence.EvidenceType,
                file_url = evidence.FileUrl,
                uploaded_at = Now,
            });
    }

    /// <summary>Shape checks that need no lock: note length, evidence type and ownership of each file.</summary>
    private async Task ValidateInput(long vendor, FoodSafetySubmitInput input, CancellationToken ct)
    {
        if (input.MenuItemIds is not { Length: > 0 })
            throw new DomainRuleException("Chọn ít nhất một món cần cấp giấy ATTP.");
        if (input.Note?.Length > 500) throw new DomainRuleException("Ghi chú tối đa 500 ký tự.");
        if (input.Evidence is not { Length: > 0 and <= MaxEvidence })
            throw new DomainRuleException($"Đính kèm từ 1 đến {MaxEvidence} giấy tờ ATTP.");
        var ownerUserId = await db.Vendors.Where(x => x.vendor_id == vendor).Select(x => x.user_id).SingleAsync(ct);
        foreach (var evidence in input.Evidence)
        {
            if (!FoodSafetyEvidenceTypes.All.Contains(evidence.EvidenceType))
                throw new DomainRuleException("Loại giấy tờ ATTP không hợp lệ.");
            if (!EvidenceFiles.TryParseUrl(evidence.FileUrl, out var owner, out var fileName) || owner != ownerUserId ||
                !await storage.ExistsAsync(EvidenceFiles.StoragePath(owner, fileName), ct))
                throw new DomainRuleException("Giấy tờ đính kèm không hợp lệ; tải lên lại.");
        }
    }

    /// <summary>Every dish belongs to the vendor's stall, is on the menu, and is not claimed by another file.</summary>
    private async Task CheckDishes(long vendor, FoodSafetySubmitInput input, long? exceptApplicationId, CancellationToken ct)
    {
        if (!await db.Storefronts.AnyAsync(x => x.storefront_id == input.StorefrontId && x.registration.vendor_id == vendor, ct))
            throw new NotFoundException("Không tìm thấy gian hàng của bạn.");
        var ids = input.MenuItemIds.Distinct().ToArray();
        var found = await db.MenuItems.CountAsync(x => ids.Contains(x.menu_item_id) &&
            x.storefront_id == input.StorefrontId && x.availability_status != "ARCHIVED", ct);
        if (found != ids.Length) throw new DomainRuleException("Có món không thuộc thực đơn của gian hàng này.");
        var today = Today;
        var claimed = await db.FoodSafetyApplicationItems
            .Where(x => ids.Contains(x.menu_item_id) && x.application_id != exceptApplicationId &&
                (FoodSafetyStatuses.Open.Contains(x.application.application_status) ||
                 (x.application.application_status == FoodSafetyStatuses.Approved && x.application.expires_on >= today)))
            .Select(x => x.menu_item.item_name).Distinct().ToListAsync(ct);
        if (claimed.Count > 0)
            throw new ConflictException($"Món đã có hồ sơ đang xét hoặc giấy còn hạn: {string.Join(", ", claimed)}.");
    }

    private async Task Notify(FoodSafetyApplication row, string title, string body, CancellationToken ct)
    {
        var userId = await db.Vendors.Where(x => x.vendor_id == row.vendor_id).Select(x => x.user_id).SingleAsync(ct);
        db.Notifications.Add(new Notification
        {
            user_id = userId,
            notification_type = "FOOD_SAFETY",
            title = title,
            body = body,
            related_entity_type = "food-safety",
            related_entity_id = row.application_id,
            sent_at = Now,
        });
    }

    private async Task<IReadOnlyList<FoodSafetyApplicationDto>> Load(IReadOnlyList<long> ids, bool forWard, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var rows = await db.FoodSafetyApplications.AsNoTracking().Where(x => ids.Contains(x.application_id))
            .Select(x => new
            {
                Row = x,
                StorefrontName = x.storefront.storefront_name,
                VendorName = x.vendor.user.full_name,
                Dishes = x.FoodSafetyApplicationItems.OrderBy(i => i.menu_item_id).Select(i => new FoodSafetyDishDto(
                    i.menu_item_id, i.menu_item.item_name, i.menu_item.category.category_name, i.menu_item.image_url)).ToList(),
                Evidence = x.FoodSafetyEvidences.OrderBy(e => e.evidence_id).Select(e => new FoodSafetyEvidenceDto(
                    e.evidence_type, e.file_url, e.uploaded_at)).ToList(),
            }).ToListAsync(ct);
        var today = Today;
        var position = ids.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        return rows.OrderBy(x => position[x.Row.application_id]).Select(x =>
        {
            var r = x.Row;
            return new FoodSafetyApplicationDto(r.application_id, r.storefront_id, x.StorefrontName,
                x.VendorName ?? $"Hộ kinh doanh #{r.vendor_id}", r.application_status, r.vendor_note, r.review_reason,
                Utc(r.reviewed_at), Utc(r.forwarded_at), r.department_name, r.certificate_number, r.issued_on, r.expires_on,
                r.application_status == FoodSafetyStatuses.Approved && r.expires_on < today,
                r.result_reason, Utc(r.result_recorded_at), Utc(r.submitted_at)!.Value,
                x.Dishes, x.Evidence.Select(e => e with { UploadedAt = Utc(e.UploadedAt)!.Value }).ToList(),
                forWard ? WardActions(r.application_status) : VendorActions(r.application_status));
        }).ToList();
    }

    private static DateTime? Utc(DateTime? value) =>
        value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;

    private Task Write(Func<Task> action, CancellationToken ct) => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await action();
        await transaction.CommitAsync(ct);
    });
}
