using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Services;

public sealed class WardSlots(
    StreetBizDbContext db,
    IGeolocation geo,
    ISidewalkPolicy sidewalkPolicy,
    TimeProvider clock)
    : IWardSlots
{
    private static readonly TimeZoneInfo VietnamTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        "Asia/Ho_Chi_Minh",
        TimeSpan.FromHours(7),
        "Vietnam Standard Time",
        "Vietnam Standard Time");

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(clock.GetUtcNow(), VietnamTimeZone).DateTime);
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static bool Open(string? status) =>
        status is AddressChangeStatuses.Pending
            or AddressChangeStatuses.UnderReview
            or TransferStatuses.AcceptedByReceiver;

    private async Task<WardActor?> ResolveAsync(long userId, CancellationToken ct)
    {
        var user = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.user_id == userId, ct);
        if (user is null
            || user.role_code != RoleCodes.WardAuthority
            || user.account_status != AccountStatuses.Active
            || user.ward_unit_id is null)
            return null;
        return new(user.user_id, user.ward_unit_id.Value, user.full_name ?? $"Cán bộ #{user.user_id}");
    }

    private async Task CheckActor(WardActor actor, CancellationToken ct)
    {
        var current = await ResolveAsync(actor.UserId, ct);
        if (current is null || current.WardId != actor.WardId)
            throw new WardException(403, "ward_access_denied", "Tài khoản không còn quyền xử lý hồ sơ phường.");
    }

    private IQueryable<SidewalkSlot> Proposals(WardActor actor) => db.SidewalkSlots
        .Include(x => x.zone).Include(x => x.proposed_by_registration)
        .Where(x => x.zone.ward_unit_id == actor.WardId && x.source == SlotSources.VendorProposed);
    private IQueryable<AddressChangeRequest> Conflicts(WardActor actor) => db.AddressChangeRequests
        .Include(x => x.registration).Include(x => x.requested_new_slot).ThenInclude(x => x!.zone)
        .Where(x => x.registration.ward_unit_id == actor.WardId);
    private IQueryable<SlotTransferRequest> Transfers(WardActor actor) => db.SlotTransferRequests
        .Include(x => x.contract).ThenInclude(x => x.slot).ThenInclude(x => x.zone)
        .Include(x => x.from_vendor).ThenInclude(x => x.user)
        .Include(x => x.to_vendor).ThenInclude(x => x.user)
        .Where(x => x.contract.slot.zone.ward_unit_id == actor.WardId);

    public async Task<CasePage> ListAsync(WardActor actor, string kind, int page, CancellationToken ct)
    {
        await CheckActor(actor, ct);
        if (page is < 1 or > 10000) throw new WardException(400, "invalid_page", "Trang không hợp lệ.");
        var offset = (page - 1) * 20;
        var ids = kind switch
        {
            "proposals" => await Proposals(actor).OrderByDescending(x => x.created_at).ThenByDescending(x => x.slot_id)
                .Skip(offset).Take(21).Select(x => x.slot_id).ToListAsync(ct),
            "conflicts" => await Conflicts(actor).Where(x => x.requested_new_slot_id != null)
                .OrderByDescending(x => x.created_at).ThenByDescending(x => x.address_change_id)
                .Skip(offset).Take(21).Select(x => x.address_change_id).ToListAsync(ct),
            "transfers" => await Transfers(actor).OrderByDescending(x => x.initiated_at).ThenByDescending(x => x.transfer_id)
                .Skip(offset).Take(21).Select(x => x.transfer_id).ToListAsync(ct),
            _ => throw new WardException(400, "invalid_kind", "Loại hồ sơ không hợp lệ.")
        };
        var items = new List<WardCase>();
        foreach (var id in ids.Take(20))
        {
            items.Add(await GetCoreAsync(actor, kind, id, ct));
        }
        return new(items, page, ids.Count > 20);
    }

    public async Task<WardCase> GetAsync(WardActor actor, string kind, long id, CancellationToken ct)
    {
        await CheckActor(actor, ct);
        return await GetCoreAsync(actor, kind, id, ct);
    }

    private async Task<WardCase> GetCoreAsync(WardActor actor, string kind, long id, CancellationToken ct)
    {
        if (kind == "proposals")
        {
            var row = await Proposals(actor).SingleOrDefaultAsync(x => x.slot_id == id, ct);
            if (row is null) { WardRules.NotFound(); return null!; }
            var blockers = new List<string>();
            if (row.proposed_by_registration?.ward_unit_id != actor.WardId)
                blockers.Add("Đăng ký đề xuất không thuộc phường này.");
            if (row.slot_status != SlotStatuses.Available) blockers.Add("Ô đang được sử dụng hoặc bị tạm dừng.");
            var hasOccupant = await Occupied(row.slot_id, ct);
            if (hasOccupant) blockers.Add("Ô đã có hợp đồng còn hiệu lực.");
            try
            {
                if (!geo.Verify(actor.WardId, new((double)row.latitude, (double)row.longitude)).Inside)
                    blockers.Add("Tọa độ nằm ngoài ranh giới phường.");
            }
            catch (WardException ex) { blockers.Add(ex.Message); }
            var pending = row.proposal_review_status == ProposalReviewStatuses.Pending
                && row.slot_status == SlotStatuses.Available
                && !hasOccupant;
            return new(Id(id), kind, "Đề xuất vị trí mới", row.proposal_review_status ?? "",
                row.slot_code, row.proposed_by_registration?.display_name ?? "",
                row.proposed_by_registration?.declared_address ?? "Chưa có địa chỉ",
                new((double)row.latitude, (double)row.longitude), row.proposal_photo_url, row.created_at,
                row.proposal_review_reason,
                row.proposal_review_status == ProposalReviewStatuses.Pending ? blockers.ToArray() : [],
                pending ? blockers.Count == 0 ? ["APPROVE", "REJECT"] : ["REJECT"] : []);
        }
        if (kind == "conflicts")
        {
            var row = await Conflicts(actor).SingleOrDefaultAsync(x => x.address_change_id == id, ct);
            if (row is null) { WardRules.NotFound(); return null!; }
            var blockers = new List<string>();
            if (row.requested_new_slot is null || row.requested_new_slot.zone.ward_unit_id != actor.WardId)
                blockers.Add("Ô mới không thuộc phường hoặc chưa được chọn.");
            else if (!await Occupied(row.requested_new_slot.slot_id, ct))
                blockers.Add("Ô hiện không có xung đột thuê; cần xử lý qua luồng đổi địa chỉ.");
            int? position = null;
            if (row.change_status == AddressChangeStatuses.UnderReview && row.reviewed_at.HasValue)
                position = 1 + await db.AddressChangeRequests.CountAsync(x =>
                    x.requested_new_slot_id == row.requested_new_slot_id
                    && x.change_status == AddressChangeStatuses.UnderReview &&
                    (x.reviewed_at < row.reviewed_at || x.reviewed_at == row.reviewed_at && x.address_change_id < id), ct);
            return new(Id(id), kind, "Xung đột đổi địa chỉ", row.change_status, row.requested_new_slot?.slot_code ?? "",
                row.registration.display_name, row.new_address,
                row.new_latitude.HasValue && row.new_longitude.HasValue ? new((double)row.new_latitude, (double)row.new_longitude) : null,
                null, row.created_at, row.conflict_resolution_note, blockers.ToArray(),
                row.change_status == AddressChangeStatuses.Pending
                    ? blockers.Count == 0 ? ["QUEUE", "REJECT"] : ["REJECT"]
                    : row.change_status == AddressChangeStatuses.UnderReview ? ["REJECT"] : [],
                position);
        }
        if (kind == "transfers")
        {
            var row = await Transfers(actor).SingleOrDefaultAsync(x => x.transfer_id == id, ct);
            if (row is null) { WardRules.NotFound(); return null!; }
            var (blockers, debt) = await TransferChecks(actor, row, ct);
            return new(Id(id), kind, "Chuyển nhượng ô", row.transfer_status, row.contract.slot.slot_code,
                row.from_vendor.user.full_name ?? Id(row.from_vendor_id),
                $"Bên nhận: {row.to_vendor.user.full_name ?? Id(row.to_vendor_id)}",
                new((double)row.contract.slot.latitude, (double)row.contract.slot.longitude),
                null, row.initiated_at, row.review_decision_reason, Open(row.transfer_status) ? blockers : [],
                Open(row.transfer_status) ? blockers.Length == 0 ? ["APPROVE", "REJECT"] : ["REJECT"] : [],
                ContractTerm: $"{row.contract.start_date:dd/MM/yyyy} – {row.contract.end_date:dd/MM/yyyy}", Outstanding: debt);
        }
        throw new WardException(400, "invalid_kind", "Loại hồ sơ không hợp lệ.");
    }

    private Task<bool> Occupied(long slotId, CancellationToken ct) => db.RentalContracts.AnyAsync(x =>
        x.slot_id == slotId
        && (x.contract_status == ContractStatuses.Active || x.contract_status == ContractStatuses.Suspended)
        && x.end_date >= Today, ct);

    private async Task<(string[] Blockers, decimal Debt)> TransferChecks(WardActor actor, SlotTransferRequest row, CancellationToken ct)
    {
        var blockers = new List<string>();
        if (row.transfer_status != TransferStatuses.AcceptedByReceiver || row.accepted_at is null)
            blockers.Add("Bên nhận chưa chấp nhận chuyển nhượng.");
        if (row.contract.contract_status != ContractStatuses.Active
            || row.contract.start_date > Today
            || row.contract.end_date < Today)
            blockers.Add("Hợp đồng không hoạt động hoặc không còn trong thời hạn.");
        if (row.contract.vendor_id != row.from_vendor_id || row.from_vendor_id == row.to_vendor_id)
            blockers.Add("Chủ hợp đồng đã thay đổi hoặc bên nhận không hợp lệ.");
        if (row.to_vendor.user.account_status != AccountStatuses.Active
            || row.from_vendor.user.account_status != AccountStatuses.Active)
            blockers.Add("Tài khoản bên chuyển hoặc bên nhận không hoạt động.");
        var registrations = await db.BusinessRegistrations.Where(x => x.vendor_id == row.to_vendor_id &&
            x.registration_status == RegistrationStatuses.Approved
            && x.ward_unit_id == actor.WardId).ToListAsync(ct);
        var eligible = false;
        foreach (var registration in registrations)
        {
            if (registration.vendor_type == VendorTypes.Itinerant) { eligible = true; break; }
            if (registration.vendor_type != VendorTypes.FixedStorefront || !registration.address_latitude.HasValue ||
                !registration.address_longitude.HasValue) continue;
            var radius = sidewalkPolicy.AdjacentRadiusMeters;
            if (radius <= 0) continue;
            var meters = Distance((double)registration.address_latitude, (double)registration.address_longitude,
                (double)row.contract.slot.latitude, (double)row.contract.slot.longitude);
            var alreadyHasSlot = await db.RentalContracts.AnyAsync(x => x.application.registration_id == registration.registration_id &&
                x.contract_id != row.contract_id
                && (x.contract_status == ContractStatuses.Active || x.contract_status == ContractStatuses.Suspended)
                && x.end_date >= Today, ct);
            if (meters <= radius && !alreadyHasSlot) { eligible = true; break; }
        }
        if (!eligible) blockers.Add("Bên nhận chưa có đăng ký được duyệt phù hợp trong phường; cửa hàng cố định cần ô liền kề và không có ô khác.");
        var fees = await db.FeeScheduleItems.Where(x => x.fee_schedule.contract_id == row.contract_id &&
            x.item_status != DebtStatuses.FeeItemPaid).Select(x => x.amount).ToListAsync(ct);
        var penalties = await db.Penalties.Where(x => (x.violation.contract_id == row.contract_id ||
            x.violation.slot_id == row.contract.slot_id) && x.penalty_status == DebtStatuses.PenaltyUnpaid)
            .Select(x => x.amount).ToListAsync(ct);
        var debt = fees.Sum() + penalties.Sum();
        if (debt > 0) blockers.Add("Ô còn phí hoặc tiền phạt chưa thanh toán.");
        if (await db.Storefronts.AnyAsync(x => x.contract_id == row.contract_id, ct))
            blockers.Add("Hợp đồng đang liên kết gian hàng Phase 2; cần xử lý quyền sở hữu gian hàng trước khi chuyển.");
        return (blockers.ToArray(), debt);
    }

    internal static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        const double radians = Math.PI / 180;
        var a = Math.Pow(Math.Sin((lat2 - lat1) * radians / 2), 2) +
            Math.Cos(lat1 * radians) * Math.Cos(lat2 * radians) * Math.Pow(Math.Sin((lon2 - lon1) * radians / 2), 2);
        return 6371000 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }

    public async Task<WardCase> DecideAsync(WardActor actor, string kind, long id, ReviewDecision decision, CancellationToken ct)
    {
        WardRules.Decision(decision);
        await Write(async () =>
        {
            var current = await GetAsync(actor, kind, id, ct);
            if (current.Status != decision.ExpectedStatus || !current.Actions.Contains(decision.Decision))
                WardRules.Conflict("Hồ sơ đã thay đổi hoặc chưa đủ điều kiện. Tải lại hồ sơ để kiểm tra.");
            var reason = decision.Reason.Trim();
            if (kind == WardCaseKinds.Proposals)
            {
                var row = await Proposals(actor).SingleAsync(x => x.slot_id == id, ct);
                row.proposal_review_status = decision.Decision == "APPROVE"
                    ? ProposalReviewStatuses.Approved
                    : ProposalReviewStatuses.Rejected;
                row.proposal_reviewed_by = actor.UserId;
                row.proposal_review_reason = reason;
                // proposal_review_status gates vendor proposals in SIDE-01. Rejection must not
                // overload slot_status, which describes physical availability/occupancy.
                row.slot_status = SlotStatuses.Available;
                if (row.proposed_by_registration is not null)
                    await NotifyVendor(row.proposed_by_registration.vendor_id, kind, id, reason, ct);
            }
            else if (kind == WardCaseKinds.Conflicts)
            {
                var row = await Conflicts(actor).SingleAsync(x => x.address_change_id == id, ct);
                row.change_status = decision.Decision == "QUEUE"
                    ? AddressChangeStatuses.UnderReview
                    : AddressChangeStatuses.Rejected;
                row.conflict_resolution_note = reason;
                row.reviewed_by = actor.UserId;
                row.reviewed_at = Now;
                // Queuing does not displace the current tenant or release the old contract.
                await NotifyVendor(row.registration.vendor_id, kind, id, reason, ct);
            }
            else
            {
                var row = await Transfers(actor).SingleAsync(x => x.transfer_id == id, ct);
                row.transfer_status = decision.Decision == "APPROVE"
                    ? TransferStatuses.Approved
                    : TransferStatuses.Rejected;
                row.reviewed_by = actor.UserId;
                row.reviewed_at = Now;
                row.review_decision_reason = reason;
                if (decision.Decision == "APPROVE")
                {
                    row.contract.vendor_id = row.to_vendor_id;
                    row.contract.updated_at = Now;
                    // The original application, dates, fees and permit remain attached to this contract.
                }
                await NotifyVendor(row.from_vendor_id, kind, id, reason, ct);
                await NotifyVendor(row.to_vendor_id, kind, id, reason, ct);
            }
            Audit(actor, kind, id, decision.Decision, new { PreviousStatus = current.Status, Reason = reason });
        }, ct);
        return await GetAsync(actor, kind, id, ct);
    }

    public async Task<WardCase> PinAsync(WardActor actor, long id, GeoPoint point, CancellationToken ct)
    {
        WardRules.Coordinates(point);
        // Normalize to the precision actually stored by SQL Server before validating.
        var normalized = new GeoPoint(Math.Round(point.Latitude, 6), Math.Round(point.Longitude, 6));
        if (!geo.Verify(actor.WardId, normalized).Inside)
            throw new WardException(422, "outside_ward", "Tọa độ nằm ngoài ranh giới phường.");
        await Write(async () =>
        {
            await CheckActor(actor, ct);
            var row = await Proposals(actor).SingleOrDefaultAsync(x => x.slot_id == id, ct);
            if (row is null) { WardRules.NotFound(); return; }
            if (row.proposal_review_status != ProposalReviewStatuses.Pending
                || row.slot_status != SlotStatuses.Available
                || await Occupied(row.slot_id, ct))
                WardRules.Conflict("Chỉ được sửa tọa độ đề xuất đang chờ và chưa được thuê.");
            var previous = new GeoPoint((double)row.latitude, (double)row.longitude);
            row.latitude = (decimal)normalized.Latitude;
            row.longitude = (decimal)normalized.Longitude;
            Audit(actor, "proposals", id, "PIN", new { Previous = previous, Point = normalized });
        }, ct);
        return await GetAsync(actor, "proposals", id, ct);
    }

    private async Task Write(Func<Task> action, CancellationToken ct)
    {
        // Serializable decisions prevent double review, stale debt checks and simultaneous transfers.
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

    private void Audit(WardActor actor, string kind, long id, string action, object details) =>
        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actor.UserId,
            action = $"WARD_{action}",
            entity_type = kind,
            entity_id = id,
            details = JsonSerializer.Serialize(details),
            created_at = Now
        });

    private async Task NotifyVendor(
        long vendorId,
        string kind,
        long id,
        string reason,
        CancellationToken ct,
        string title = "Kết quả xử lý hồ sơ vị trí")
    {
        var userId = await db.Vendors.Where(x => x.vendor_id == vendorId).Select(x => x.user_id).SingleAsync(ct);
        db.Notifications.Add(new Notification
        {
            user_id = userId,
            notification_type = "WARD_REVIEW",
            title = title,
            body = reason,
            related_entity_type = kind,
            related_entity_id = id,
            sent_at = Now
        });
    }
}
