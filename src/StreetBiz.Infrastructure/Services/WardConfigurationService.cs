using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Geo;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.SidewalkSlots.GetSlotQuote;
using StreetBiz.Application.Features.WardConfiguration;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Sidewalk;

namespace StreetBiz.Infrastructure.Services;

/// <summary>
/// Ward Configuration (WARD-01 slot grid, WARD-02 zone pricing/hours, WARD-03 penalty schedule).
/// Every write runs in one serializable transaction and leaves exactly one AuditLogs row.
/// </summary>
public sealed class WardConfigurationService(
    StreetBizDbContext db,
    IGeolocation geolocation,
    IOptions<SidewalkSettings> sidewalkOptions,
    TimeProvider clock)
    : IWardConfigurationService
{
    private static readonly TimeZoneInfo VietnamTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        "Asia/Ho_Chi_Minh", TimeSpan.FromHours(7), "Asia/Ho_Chi_Minh", "Asia/Ho_Chi_Minh");

    private static readonly JsonSerializerOptions AuditJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const string ZoneEntity = "PricingZone";
    private const string SlotEntity = "SidewalkSlot";
    private const string FeatureEntity = "StreetFeature";
    private const string PenaltyEntity = "PenaltyFeeSchedule";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private DateOnly TodayVn => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), VietnamTimeZone).DateTime);

    private SidewalkSettings Settings => sidewalkOptions.Value;

    #region WARD-03 Penalty schedule
    public async Task<IReadOnlyList<WardPenaltyTypeDto>> ListPenaltyOverviewAsync(WardActor actor, CancellationToken ct)
    {
        var types = await db.ViolationTypes.AsNoTracking()
            .OrderByDescending(t => t.is_active).ThenBy(t => t.violation_type_code)
            .ToListAsync(ct);
        var rows = await db.PenaltyFeeSchedules.AsNoTracking().Include(s => s.UserAccount)
            .Where(s => s.ward_unit_id == actor.WardId)
            .ToListAsync(ct);
        var inUse = await InUseScheduleIdsAsync(rows.Select(r => r.penalty_schedule_id).ToList(), ct);
        var today = TodayVn;
        return types.Select(t => BuildPenaltyType(t, rows.Where(r => r.violation_type == t.violation_type_code).ToList(), inUse, today)).ToList();
    }

    public async Task<IReadOnlyList<PenaltyRateDto>> ListPenaltyHistoryAsync(WardActor actor, string violationType, CancellationToken ct)
    {
        var rows = await db.PenaltyFeeSchedules.AsNoTracking().Include(s => s.UserAccount)
            .Where(s => s.ward_unit_id == actor.WardId && s.violation_type == violationType)
            .OrderByDescending(s => s.effective_from).ThenByDescending(s => s.penalty_schedule_id)
            .ToListAsync(ct);
        var inUse = await InUseScheduleIdsAsync(rows.Select(r => r.penalty_schedule_id).ToList(), ct);
        return rows.Select(r => ToRate(r, inUse)).ToList();
    }

    public async Task<WardPenaltyTypeDto> SetPenaltyRateAsync(WardActor actor, SetPenaltyRateRequest request, CancellationToken ct)
    {
        var type = await db.ViolationTypes.AsNoTracking()
            .SingleOrDefaultAsync(t => t.violation_type_code == request.ViolationType, ct)
            ?? throw new NotFoundException("Không tìm thấy loại vi phạm.");
        if (!type.is_active)
            throw new WardException(400, "violation_type_inactive", "Loại vi phạm này đã ngừng sử dụng, không thể đặt mức phạt mới.");

        var today = TodayVn;
        if (request.EffectiveFrom < today)
            throw new WardException(400, "effective_date_in_past", "Ngày áp dụng không được trước ngày hôm nay.");

        var legalBasis = LegalBasisText.Format(request.DocumentRef, request.Article, request.Clause, request.Point,
            request.Behavior, request.BracketMin, request.BracketMax);
        var amount = LegalBasisText.Midpoint(request.BracketMin, request.BracketMax);

        await Write(async () =>
        {
            var open = await db.PenaltyFeeSchedules
                .SingleOrDefaultAsync(s => s.ward_unit_id == actor.WardId
                                           && s.violation_type == request.ViolationType
                                           && s.effective_to == null, ct);

            if (open?.penalty_schedule_id != request.ExpectedCurrentScheduleId)
                throw new ConflictException("Biểu mức phạt vừa được cán bộ khác cập nhật. Vui lòng tải lại trước khi lưu.");

            if (open is not null && open.effective_from > today)
                throw new WardException(400, "rate_already_scheduled",
                    $"Đã có mức phạt hẹn áp dụng từ {open.effective_from:dd/MM/yyyy}. Hãy hủy mức đó trước khi đặt mức mới.");

            var before = open is null ? null : RateSnapshot(open);

            if (open is not null && open.effective_from == request.EffectiveFrom)
            {
                // CK_PenaltyFeeSchedules_DateOrder forbids closing a row on the day it opened, so a
                // same-day correction edits the row -- allowed only while no penalty uses it.
                if (await db.Penalties.AnyAsync(p => p.penalty_schedule_id == open.penalty_schedule_id, ct))
                    throw new WardException(400, "rate_same_day_in_use",
                        "Mức phạt áp dụng từ ngày này đã được dùng cho quyết định xử phạt, không thể sửa trong ngày. Hãy hẹn áp dụng từ ngày mai.");

                open.penalty_amount = amount;
                open.legal_basis = legalBasis;
                Audit(actor, "PENALTY_RATE_REVISED", PenaltyEntity, open.penalty_schedule_id, new { before, after = RateSnapshot(open) });
                return;
            }

            if (open is not null)
            {
                open.effective_to = request.EffectiveFrom;
                // Close first: UQ_PenaltyFeeSchedules_CurrentRate allows one open row at a time.
                await db.SaveChangesAsync(ct);
            }

            var row = new PenaltyFeeSchedule
            {
                ward_unit_id = actor.WardId,
                ward_unit_type = "WARD",
                violation_type = request.ViolationType,
                penalty_amount = amount,
                legal_basis = legalBasis,
                effective_from = request.EffectiveFrom,
                created_by = actor.UserId,
                creator_role = RoleCodes.WardAuthority,
                created_at = Now,
            };
            db.PenaltyFeeSchedules.Add(row);
            await db.SaveChangesAsync(ct);
            Audit(actor, "PENALTY_RATE_SET", PenaltyEntity, row.penalty_schedule_id, new { before, after = RateSnapshot(row) });
        }, ct);

        return await GetPenaltyTypeAsync(actor, request.ViolationType, ct);
    }

    public async Task<WardPenaltyTypeDto> CancelScheduledPenaltyRateAsync(WardActor actor, int scheduleId, CancellationToken ct)
    {
        var today = TodayVn;
        string violationType = "";

        await Write(async () =>
        {
            var row = await db.PenaltyFeeSchedules
                .SingleOrDefaultAsync(s => s.penalty_schedule_id == scheduleId && s.ward_unit_id == actor.WardId, ct)
                ?? throw new NotFoundException("Không tìm thấy mức phạt tại địa bàn phường của bạn.");
            violationType = row.violation_type;

            if (row.effective_to is not null || row.effective_from <= today)
                throw new WardException(400, "rate_not_scheduled", "Chỉ hủy được mức phạt đã hẹn và chưa đến ngày áp dụng.");
            if (await db.Penalties.AnyAsync(p => p.penalty_schedule_id == row.penalty_schedule_id, ct))
                throw new WardException(400, "rate_in_use", "Mức phạt đã được dùng cho quyết định xử phạt, không thể hủy.");

            var previous = await db.PenaltyFeeSchedules
                .Where(s => s.ward_unit_id == actor.WardId && s.violation_type == row.violation_type
                            && s.effective_to == row.effective_from)
                .OrderByDescending(s => s.effective_from)
                .FirstOrDefaultAsync(ct);

            var snapshot = RateSnapshot(row);
            db.PenaltyFeeSchedules.Remove(row);
            await db.SaveChangesAsync(ct);

            if (previous is not null)
                previous.effective_to = null;

            Audit(actor, "PENALTY_RATE_CANCELLED", PenaltyEntity, scheduleId,
                new { before = snapshot, after = (object?)null, reopenedScheduleId = previous?.penalty_schedule_id });
        }, ct);

        return await GetPenaltyTypeAsync(actor, violationType, ct);
    }

    private async Task<WardPenaltyTypeDto> GetPenaltyTypeAsync(WardActor actor, string violationType, CancellationToken ct)
    {
        var type = await db.ViolationTypes.AsNoTracking().SingleAsync(t => t.violation_type_code == violationType, ct);
        var rows = await db.PenaltyFeeSchedules.AsNoTracking().Include(s => s.UserAccount)
            .Where(s => s.ward_unit_id == actor.WardId && s.violation_type == violationType)
            .ToListAsync(ct);
        var inUse = await InUseScheduleIdsAsync(rows.Select(r => r.penalty_schedule_id).ToList(), ct);
        return BuildPenaltyType(type, rows, inUse, TodayVn);
    }

    private async Task<HashSet<int>> InUseScheduleIdsAsync(List<int> scheduleIds, CancellationToken ct) =>
        (await db.Penalties.AsNoTracking()
            .Where(p => scheduleIds.Contains(p.penalty_schedule_id))
            .Select(p => p.penalty_schedule_id)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

    private static WardPenaltyTypeDto BuildPenaltyType(ViolationType type, List<PenaltyFeeSchedule> rows, HashSet<int> inUse, DateOnly today)
    {
        var current = rows.FirstOrDefault(r => PenaltyRates.IsInForce(r.effective_from, r.effective_to, today));
        var scheduled = rows.FirstOrDefault(r => r.effective_to is null && r.effective_from > today);
        return new WardPenaltyTypeDto(
            type.violation_type_code,
            type.description,
            type.is_active,
            !string.IsNullOrWhiteSpace(current?.legal_basis),
            current is null ? null : ToRate(current, inUse),
            scheduled is null ? null : ToRate(scheduled, inUse));
    }

    private static PenaltyRateDto ToRate(PenaltyFeeSchedule r, HashSet<int> inUse)
    {
        var bracket = LegalBasisText.TryParseBracket(r.legal_basis);
        return new PenaltyRateDto(r.penalty_schedule_id, r.penalty_amount, bracket?.Min, bracket?.Max, r.legal_basis,
            r.effective_from, r.effective_to, DateTime.SpecifyKind(r.created_at, DateTimeKind.Utc), inUse.Contains(r.penalty_schedule_id),
            r.UserAccount?.full_name ?? "Cán bộ phường");
    }

    private static object RateSnapshot(PenaltyFeeSchedule r) => new
    {
        scheduleId = r.penalty_schedule_id,
        amount = r.penalty_amount,
        legalBasis = r.legal_basis,
        effectiveFrom = r.effective_from,
        effectiveTo = r.effective_to,
    };
    #endregion

    #region WARD-02 Pricing zones
    public async Task<IReadOnlyList<WardZoneDto>> ListZonesAsync(WardActor actor, CancellationToken ct)
    {
        var zones = await ZonesQuery(actor).OrderBy(z => z.zone_name).ToListAsync(ct);
        return await ToZoneDtosAsync(zones, ct);
    }

    public async Task<WardZoneDto> GetZoneAsync(WardActor actor, int zoneId, CancellationToken ct)
    {
        var zone = await ZonesQuery(actor).SingleOrDefaultAsync(z => z.zone_id == zoneId, ct)
            ?? throw ZoneNotFound();
        return (await ToZoneDtosAsync([zone], ct))[0];
    }

    public async Task<WardZoneDto> CreateZoneAsync(WardActor actor, UpsertZoneRequest request, CancellationToken ct)
    {
        var zoneId = 0;
        await Write(async () =>
        {
            await EnsureZoneNamesFreeAsync(actor, request, null, ct);
            var zone = new PricingZone
            {
                ward_unit_id = actor.WardId,
                ward_unit_type = "WARD",
                created_by = actor.UserId,
                creator_role = RoleCodes.WardAuthority,
                created_at = Now,
            };
            ApplyZoneFields(zone, request);
            zone.regulation_ref = ZoneRegulationText.Compose(request.RegulationNumber!, request.RegulationIssuedOn!.Value, request.RegulationIssuer!);
            db.PricingZones.Add(zone);
            await db.SaveChangesAsync(ct);
            AddFeeComponents(zone.zone_id, request.FeeComponents);
            zoneId = zone.zone_id;
            Audit(actor, "ZONE_CREATED", ZoneEntity, zone.zone_id,
                new { before = (object?)null, after = ZoneSnapshot(zone, request.FeeComponents), reason = request.ChangeReason });
        }, ct);
        return await GetZoneAsync(actor, zoneId, ct);
    }

    public async Task<WardZoneDto> UpdateZoneAsync(WardActor actor, int zoneId, UpsertZoneRequest request, CancellationToken ct)
    {
        await Write(async () =>
        {
            var zone = await db.PricingZones.Include(z => z.ZoneFeeComponents)
                .SingleOrDefaultAsync(z => z.zone_id == zoneId && z.ward_unit_id == actor.WardId, ct)
                ?? throw ZoneNotFound();
            EnsureToken(ZoneToken(zone, zone.ZoneFeeComponents), request.VersionToken);
            await EnsureZoneNamesFreeAsync(actor, request, zoneId, ct);

            var oldFeeComponents = zone.ZoneFeeComponents.OrderBy(c => c.sort_order).ThenBy(c => c.component_id)
                .Select(c => new ZoneFeeComponentInput(c.component_name, c.calc_basis, (long)c.unit_amount)).ToList();
            var before = ZoneSnapshot(zone, oldFeeComponents);
            var oldPrice = zone.price_per_day;
            var oldFrom = zone.available_from;
            var oldTo = zone.available_to;
            // Component ids are server-assigned and irrelevant to "did this change" -- compare only
            // what the vendor is actually charged: name, calc basis and amount, in order.
            var feeComponentsChanged = !oldFeeComponents
                .Select(c => (c.ComponentName, c.CalcBasis, c.UnitAmount))
                .SequenceEqual(request.FeeComponents.Select(c => (c.ComponentName, c.CalcBasis, c.UnitAmount)));

            ApplyZoneFields(zone, request);
            if (request.RegulationNumber is not null)
                zone.regulation_ref = ZoneRegulationText.Compose(request.RegulationNumber, request.RegulationIssuedOn!.Value, request.RegulationIssuer!);
            if (string.IsNullOrWhiteSpace(zone.regulation_ref))
                throw new WardException(400, "regulation_required", "Khu vực chưa có văn bản cho phép: nhập đủ số hiệu, ngày và cơ quan ban hành.");

            db.ZoneFeeComponents.RemoveRange(zone.ZoneFeeComponents);
            AddFeeComponents(zone.zone_id, request.FeeComponents);

            var priceChanged = oldPrice != zone.price_per_day;
            var hoursChanged = oldFrom != zone.available_from || oldTo != zone.available_to;
            var notified = await NotifyZoneChangeAsync(zone, priceChanged, oldPrice, feeComponentsChanged, hoursChanged, ct);

            Audit(actor, "ZONE_UPDATED", ZoneEntity, zone.zone_id,
                new { before, after = ZoneSnapshot(zone, request.FeeComponents), reason = request.ChangeReason, vendorsNotified = notified });
        }, ct);
        return await GetZoneAsync(actor, zoneId, ct);
    }

    public async Task DeleteZoneAsync(WardActor actor, int zoneId, string versionToken, CancellationToken ct)
    {
        await Write(async () =>
        {
            var zone = await db.PricingZones.Include(z => z.ZoneFeeComponents)
                .SingleOrDefaultAsync(z => z.zone_id == zoneId && z.ward_unit_id == actor.WardId, ct)
                ?? throw ZoneNotFound();
            EnsureToken(ZoneToken(zone, zone.ZoneFeeComponents), versionToken);
            if (await db.SidewalkSlots.AnyAsync(s => s.zone_id == zoneId, ct))
                throw new WardException(400, "zone_has_slots", "Khu vực còn ô sạp, không thể xóa.");
            if (await db.StreetFeatures.AnyAsync(f => f.zone_id == zoneId, ct))
                throw new WardException(400, "zone_has_features", "Khu vực còn chướng ngại vật đã khai báo, hãy xóa chúng trước.");

            var before = ZoneSnapshot(zone, zone.ZoneFeeComponents
                .Select(c => new ZoneFeeComponentInput(c.component_name, c.calc_basis, (long)c.unit_amount)).ToList());
            db.ZoneFeeComponents.RemoveRange(zone.ZoneFeeComponents);
            db.PricingZones.Remove(zone);
            Audit(actor, "ZONE_DELETED", ZoneEntity, zoneId, new { before, after = (object?)null });
        }, ct);
    }

    public async Task<ZoneImpactPreviewDto> PreviewZoneImpactAsync(WardActor actor, int zoneId, ZoneImpactPreviewRequest request, CancellationToken ct)
    {
        var zone = await ZonesQuery(actor).SingleOrDefaultAsync(z => z.zone_id == zoneId, ct) ?? throw ZoneNotFound();
        var hoursChanged = zone.available_from != request.AvailableFrom || zone.available_to != request.AvailableTo;

        // The real total a pending application or renewal will be charged (once WARD-08/WARD-09
        // approves it) is price-per-day PLUS every fee component -- the same formula
        // GenerateFeeScheduleCommand uses for the real fee schedule. Comparing only price_per_day
        // here would miss a fee-only edit entirely and under-report every price edit's delta.
        var currentComponents = ToFeeComponentRows(zone.ZoneFeeComponents
            .OrderBy(c => c.sort_order).ThenBy(c => c.component_id));
        var newComponents = ToFeeComponentRows(request.FeeComponents);
        // Component ids are server-assigned and meaningless for "did this change" -- compare only
        // what the vendor is actually charged: name, calc basis and amount, in order.
        var feeComponentsChanged = !currentComponents
            .Select(c => (c.ComponentName, c.CalcBasis, c.UnitAmount))
            .SequenceEqual(newComponents.Select(c => (c.ComponentName, c.CalcBasis, c.UnitAmount)));
        var amountChanged = zone.price_per_day != request.PricePerDay || feeComponentsChanged;

        var impact = await LoadZoneImpactAsync(zoneId, ct);

        decimal TotalFor(decimal pricePerDay, IReadOnlyList<Application.Common.Models.FeeComponentRow> components, int termDays) =>
            FeeQuoteCalculator.Calculate(0, pricePerDay, termDays, components).Total;

        var apps = impact.Applications.Select(a => new ZoneImpactItem("RENTAL_APPLICATION", a.Id, a.SlotCode, a.VendorName, a.TermDays,
            TotalFor(zone.price_per_day, currentComponents, a.TermDays), TotalFor(request.PricePerDay, newComponents, a.TermDays))).ToList();
        var renewals = impact.Renewals.Select(r => new ZoneImpactItem("RENEWAL", r.Id, r.SlotCode, r.VendorName, r.TermDays,
            TotalFor(zone.price_per_day, currentComponents, r.TermDays), TotalFor(request.PricePerDay, newComponents, r.TermDays))).ToList();

        var recipients = RecipientsFor(impact, amountChanged, hoursChanged);
        return new ZoneImpactPreviewDto(
            amountChanged,
            hoursChanged,
            apps,
            renewals,
            hoursChanged ? impact.ActiveContractUserIds.Count : 0,
            apps.Sum(a => a.NewTotal - a.CurrentTotal) + renewals.Sum(r => r.NewTotal - r.CurrentTotal),
            recipients.Count);
    }

    private static List<Application.Common.Models.FeeComponentRow> ToFeeComponentRows(IEnumerable<ZoneFeeComponent> components) =>
        components.Select((c, i) => new Application.Common.Models.FeeComponentRow(c.component_id, c.component_name, c.calc_basis, c.unit_amount, i)).ToList();

    private static List<Application.Common.Models.FeeComponentRow> ToFeeComponentRows(IEnumerable<ZoneFeeComponentInput> components) =>
        components.Select((c, i) => new Application.Common.Models.FeeComponentRow(0, c.ComponentName, c.CalcBasis, c.UnitAmount, i)).ToList();

    public async Task<IReadOnlyList<ConfigHistoryEntryDto>> ListZoneHistoryAsync(WardActor actor, int zoneId, CancellationToken ct)
    {
        // A deleted zone no longer exists, so ownership is checked against the zone row while it lives.
        if (!await ZonesQuery(actor).AnyAsync(z => z.zone_id == zoneId, ct))
            throw ZoneNotFound();

        var entries = await db.AuditLogs.AsNoTracking()
            .Where(a => a.entity_type == ZoneEntity && a.entity_id == zoneId)
            .OrderByDescending(a => a.created_at).ThenByDescending(a => a.audit_id)
            .Select(a => new { a.audit_id, a.action, a.actor_user.full_name, a.created_at, a.details })
            .ToListAsync(ct);
        return entries.Select(a => new ConfigHistoryEntryDto(a.audit_id, a.action, a.full_name ?? "Cán bộ phường",
            DateTime.SpecifyKind(a.created_at, DateTimeKind.Utc), a.details)).ToList();
    }

    private IQueryable<PricingZone> ZonesQuery(WardActor actor) =>
        db.PricingZones.AsNoTracking().Include(z => z.ZoneFeeComponents).Where(z => z.ward_unit_id == actor.WardId);

    private async Task<List<WardZoneDto>> ToZoneDtosAsync(IReadOnlyList<PricingZone> zones, CancellationToken ct)
    {
        var ids = zones.Select(z => z.zone_id).ToList();
        var slotCounts = await db.SidewalkSlots.AsNoTracking()
            .Where(s => ids.Contains(s.zone_id) && s.source == SlotSources.WardDefined
                        || ids.Contains(s.zone_id) && s.proposal_review_status == ProposalReviewStatuses.Approved)
            .GroupBy(s => s.zone_id)
            .Select(g => new { ZoneId = g.Key, Total = g.Count(), Active = g.Count(s => s.slot_status == SlotStatuses.Active) })
            .ToListAsync(ct);
        var featureCounts = await db.StreetFeatures.AsNoTracking()
            .Where(f => ids.Contains(f.zone_id))
            .GroupBy(f => f.zone_id)
            .Select(g => new { ZoneId = g.Key, Total = g.Count() })
            .ToListAsync(ct);

        return zones.Select(z =>
        {
            var slots = slotCounts.FirstOrDefault(s => s.ZoneId == z.zone_id);
            var components = z.ZoneFeeComponents.OrderBy(c => c.sort_order).ThenBy(c => c.component_id).ToList();
            return new WardZoneDto(
                z.zone_id, z.zone_name, z.zone_code, z.price_per_day, z.available_from, z.available_to,
                ZoneHours.IsOvernight(z.available_from, z.available_to), z.regulation_ref, z.segment_from, z.segment_to,
                z.application_deadline, slots?.Total ?? 0, slots?.Active ?? 0,
                featureCounts.FirstOrDefault(f => f.ZoneId == z.zone_id)?.Total ?? 0,
                components.Select(c => new ZoneFeeComponentView(c.component_id, c.component_name, c.calc_basis, c.unit_amount, c.sort_order)).ToList(),
                ZoneToken(z, components),
                z.price_display_unit, z.price_per_month, z.rental_mode, z.event_start_date, z.event_end_date);
        }).ToList();
    }

    private async Task EnsureZoneNamesFreeAsync(WardActor actor, UpsertZoneRequest request, int? selfId, CancellationToken ct)
    {
        var name = request.ZoneName.Trim();
        var code = request.ZoneCode.Trim();
        if (await db.PricingZones.AnyAsync(z => z.ward_unit_id == actor.WardId && z.zone_name == name && z.zone_id != selfId, ct))
            throw new WardException(400, "zone_name_taken", "Tên khu vực đã tồn tại trong phường.");
        if (await db.PricingZones.AnyAsync(z => z.ward_unit_id == actor.WardId && z.zone_code == code && z.zone_id != selfId, ct))
            throw new WardException(400, "zone_code_taken", "Mã khu vực đã tồn tại trong phường.");
    }

    private static void ApplyZoneFields(PricingZone zone, UpsertZoneRequest r)
    {
        zone.zone_name = r.ZoneName.Trim();
        zone.zone_code = r.ZoneCode.Trim();
        zone.available_from = r.AvailableFrom;
        zone.available_to = r.AvailableTo;
        zone.segment_from = NullIfBlank(r.SegmentFrom);
        zone.segment_to = NullIfBlank(r.SegmentTo);
        zone.application_deadline = r.ApplicationDeadline;

        // price_per_day stays the only value FeeQuoteCalculator/FeeInstalmentPlanner read.
        // Entering a monthly price derives it instead of taking the submitted PricePerDay
        // as-is, so the two can never silently disagree.
        zone.price_display_unit = r.PriceDisplayUnit;
        if (r.PriceDisplayUnit == PriceDisplayUnits.Month)
        {
            zone.price_per_month = r.PricePerMonth;
            zone.price_per_day = decimal.Round((r.PricePerMonth ?? 0) / 30m, 0, MidpointRounding.AwayFromZero);
        }
        else
        {
            zone.price_per_month = null;
            zone.price_per_day = r.PricePerDay;
        }

        zone.rental_mode = r.RentalMode;
        zone.event_start_date = r.RentalMode == RentalModes.Event ? r.EventStartDate : null;
        zone.event_end_date = r.RentalMode == RentalModes.Event ? r.EventEndDate : null;
    }

    private void AddFeeComponents(int zoneId, IReadOnlyList<ZoneFeeComponentInput> components)
    {
        for (var i = 0; i < components.Count; i++)
        {
            db.ZoneFeeComponents.Add(new ZoneFeeComponent
            {
                zone_id = zoneId,
                component_name = components[i].ComponentName.Trim(),
                calc_basis = components[i].CalcBasis,
                unit_amount = components[i].UnitAmount,
                sort_order = i,
            });
        }
    }

    private sealed record ImpactRow(long Id, string SlotCode, string VendorName, int TermDays, long UserId);

    private sealed record ZoneImpact(List<ImpactRow> Applications, List<ImpactRow> Renewals, HashSet<long> ActiveContractUserIds);

    private async Task<ZoneImpact> LoadZoneImpactAsync(int zoneId, CancellationToken ct)
    {
        var apps = await db.RentalApplications.AsNoTracking()
            .Where(a => a.slot.zone_id == zoneId && ApplicationStatuses.Open.Contains(a.application_status))
            .Select(a => new ImpactRow(a.application_id, a.slot.slot_code, a.registration.display_name, a.requested_term_days, a.registration.vendor.user_id))
            .ToListAsync(ct);
        var renewals = await db.RenewalRequests.AsNoTracking()
            .Where(r => r.contract.slot.zone_id == zoneId && RenewalStatuses.Open.Contains(r.renewal_status))
            .Select(r => new ImpactRow(r.renewal_id, r.contract.slot.slot_code, r.contract.application.registration.display_name,
                r.requested_term_days, r.contract.vendor.user_id))
            .ToListAsync(ct);
        var activeUsers = await db.RentalContracts.AsNoTracking()
            .Where(c => c.slot.zone_id == zoneId && c.contract_status == ContractStatuses.Active)
            .Select(c => c.vendor.user_id)
            .Distinct()
            .ToListAsync(ct);
        return new ZoneImpact(apps, renewals, activeUsers.ToHashSet());
    }

    private static HashSet<long> RecipientsFor(ZoneImpact impact, bool priceChanged, bool hoursChanged)
    {
        var users = new HashSet<long>();
        if (priceChanged)
        {
            users.UnionWith(impact.Applications.Select(a => a.UserId));
            users.UnionWith(impact.Renewals.Select(r => r.UserId));
        }
        if (hoursChanged)
        {
            users.UnionWith(impact.ActiveContractUserIds);
            users.UnionWith(impact.Applications.Select(a => a.UserId));
        }
        return users;
    }

    private async Task<int> NotifyZoneChangeAsync(
        PricingZone zone, bool priceChanged, decimal oldPrice, bool feeComponentsChanged, bool hoursChanged, CancellationToken ct)
    {
        var amountChanged = priceChanged || feeComponentsChanged;
        if (!amountChanged && !hoursChanged) return 0;
        var impact = await LoadZoneImpactAsync(zone.zone_id, ct);
        var vi = CultureInfo.GetCultureInfo("vi-VN");

        void Send(IEnumerable<long> users, string type, string title, string body)
        {
            foreach (var user in users.Distinct())
            {
                db.Notifications.Add(new Notification
                {
                    user_id = user,
                    notification_type = type,
                    title = title,
                    body = body,
                    related_entity_type = ZoneEntity,
                    related_entity_id = zone.zone_id,
                    is_read = false,
                    sent_at = Now,
                });
            }
        }

        if (priceChanged)
        {
            Send(impact.Applications.Select(a => a.UserId).Concat(impact.Renewals.Select(r => r.UserId)),
                "ZONE_PRICE_CHANGED",
                $"Khu vực {zone.zone_name} thay đổi giá thuê",
                $"Giá thuê khu vực {zone.zone_name} đổi từ {oldPrice.ToString("N0", vi)}đ lên {zone.price_per_day.ToString("N0", vi)}đ/ngày. " +
                "Giá mới áp dụng cho đơn thuê và đơn gia hạn được duyệt từ nay; hợp đồng và biểu phí đã phát hành không thay đổi.");
        }
        else if (feeComponentsChanged)
        {
            Send(impact.Applications.Select(a => a.UserId).Concat(impact.Renewals.Select(r => r.UserId)),
                "ZONE_FEES_CHANGED",
                $"Khu vực {zone.zone_name} thay đổi phụ phí",
                $"Phụ phí cố định tại khu vực {zone.zone_name} vừa được cập nhật, làm thay đổi tổng số tiền phải nộp. " +
                "Phụ phí mới áp dụng cho đơn thuê và đơn gia hạn được duyệt từ nay; hợp đồng và biểu phí đã phát hành không thay đổi.");
        }

        if (hoursChanged)
        {
            var window = zone.available_from is null
                ? "không giới hạn khung giờ"
                : $"{zone.available_from:HH\\:mm} - {zone.available_to:HH\\:mm}" +
                  (ZoneHours.IsOvernight(zone.available_from, zone.available_to) ? " (qua đêm)" : "");
            Send(impact.ActiveContractUserIds.Concat(impact.Applications.Select(a => a.UserId)),
                "ZONE_HOURS_CHANGED",
                $"Khu vực {zone.zone_name} thay đổi khung giờ kinh doanh",
                $"Khung giờ kinh doanh tại khu vực {zone.zone_name} từ nay là {window}.");
        }

        return RecipientsFor(impact, amountChanged, hoursChanged).Count;
    }

    private static object ZoneSnapshot(PricingZone z, IReadOnlyList<ZoneFeeComponentInput> components) => new
    {
        zoneName = z.zone_name,
        zoneCode = z.zone_code,
        pricePerDay = z.price_per_day,
        availableFrom = z.available_from,
        availableTo = z.available_to,
        regulationRef = z.regulation_ref,
        segmentFrom = z.segment_from,
        segmentTo = z.segment_to,
        applicationDeadline = z.application_deadline,
        feeComponents = components,
    };

    private static string ZoneToken(PricingZone z, IEnumerable<ZoneFeeComponent> components) =>
        VersionToken.Compute(
            z.zone_name, z.zone_code, z.price_per_day, z.available_from, z.available_to, z.regulation_ref,
            z.segment_from, z.segment_to, z.application_deadline,
            string.Join(";", components.OrderBy(c => c.sort_order).ThenBy(c => c.component_id)
                .Select(c => VersionToken.Compute(c.component_name, c.calc_basis, c.unit_amount))));

    private static NotFoundException ZoneNotFound() => new("Không tìm thấy khu vực tại địa bàn phường của bạn.");
    #endregion

    #region WARD-01 Slot grid & street features
    public async Task<WardSlotGridDto> GetSlotGridAsync(WardActor actor, int? zoneId, CancellationToken ct)
    {
        var slots = await GridSlotsQuery(actor)
            .Where(s => zoneId == null || s.zone_id == zoneId)
            .OrderBy(s => s.slot_code)
            .ToListAsync(ct);
        var features = await db.StreetFeatures.AsNoTracking()
            .Where(f => f.zone.ward_unit_id == actor.WardId && (zoneId == null || f.zone_id == zoneId))
            .OrderBy(f => f.feature_id)
            .ToListAsync(ct);

        return new WardSlotGridDto(
            await ToSlotDtosAsync(slots, ct),
            features.Select(ToFeatureDto).ToList(),
            BoundaryConfigured(actor.WardId),
            Settings.FeatureClearanceEnabled);
    }

    public async Task<PlacementCheckDto> CheckPlacementAsync(WardActor actor, SlotPlacementInput input, long? ignoreSlotId, CancellationToken ct)
    {
        await RequireZoneAsync(actor, input.ZoneId, ct);
        var context = await LoadPlacementContextAsync(actor, ct);
        return Evaluate(actor, input, ignoreSlotId, context);
    }

    public async Task<SlotMutationResultDto> CreateSlotAsync(WardActor actor, CreateSlotRequest request, CancellationToken ct)
    {
        var input = new SlotPlacementInput(request.ZoneId, request.Latitude, request.Longitude, request.WidthMeters, request.LengthMeters);
        long slotId = 0;
        PlacementCheckDto check = null!;

        await Write(async () =>
        {
            var zone = await RequireZoneAsync(actor, request.ZoneId, ct);
            check = Evaluate(actor, input, null, await LoadPlacementContextAsync(actor, ct));
            EnsurePlacementAccepted(check, request.AcknowledgeWarnings);

            var code = string.IsNullOrWhiteSpace(request.SlotCode)
                ? (await NextSlotCodesAsync(zone, 1, ct))[0]
                : await RequireFreeSlotCodeAsync(request.SlotCode.Trim(), null, ct);

            var slot = new SidewalkSlot
            {
                slot_code = code,
                zone_id = zone.zone_id,
                latitude = request.Latitude,
                longitude = request.Longitude,
                width_meters = request.WidthMeters,
                length_meters = request.LengthMeters,
                slot_status = SlotStatuses.Available,
                source = SlotSources.WardDefined,
                has_power = request.HasPower,
                has_water = request.HasWater,
                has_trash_bin = request.HasTrashBin,
                business_category = request.BusinessCategory,
                created_at = Now,
            };
            db.SidewalkSlots.Add(slot);
            await db.SaveChangesAsync(ct);
            slotId = slot.slot_id;
            Audit(actor, "SLOT_CREATED", SlotEntity, slot.slot_id, new
            {
                before = (object?)null,
                after = SlotSnapshot(slot),
                boundaryVerified = check.BoundaryVerified,
                warningsAcknowledged = AcknowledgedWarnings(check),
                reason = request.WarningReason,
            });
        }, ct);

        return new SlotMutationResultDto(await GetSlotDtoAsync(actor, slotId, ct), check);
    }

    public async Task<SlotMutationResultDto> UpdateSlotAsync(WardActor actor, long slotId, UpdateSlotRequest request, CancellationToken ct)
    {
        PlacementCheckDto check = new(true, []);

        await Write(async () =>
        {
            var slot = await RequireSlotAsync(actor, slotId, ct);
            if (slot.source == SlotSources.VendorProposed)
                throw new WardException(400, "vendor_proposed_readonly", "Ô do hộ kinh doanh đề xuất được xử lý ở luồng duyệt đề xuất (WARD-16), không sửa tại đây.");
            EnsureToken(SlotToken(slot), request.VersionToken);

            var geometryChanged = slot.zone_id != request.ZoneId || slot.latitude != request.Latitude
                                  || slot.longitude != request.Longitude || slot.width_meters != request.WidthMeters
                                  || slot.length_meters != request.LengthMeters || slot.slot_code != request.SlotCode.Trim();
            if (geometryChanged && slot.slot_status != SlotStatuses.Available)
                throw new WardException(400, "slot_in_use", "Chỉ sửa mã, vị trí, kích thước hoặc khu vực khi ô đang trống (AVAILABLE).");

            if (geometryChanged)
            {
                await RequireZoneAsync(actor, request.ZoneId, ct);
                check = Evaluate(actor,
                    new SlotPlacementInput(request.ZoneId, request.Latitude, request.Longitude, request.WidthMeters, request.LengthMeters),
                    slot.slot_id, await LoadPlacementContextAsync(actor, ct));
                EnsurePlacementAccepted(check, request.AcknowledgeWarnings);
                if (slot.slot_code != request.SlotCode.Trim())
                    slot.slot_code = await RequireFreeSlotCodeAsync(request.SlotCode.Trim(), slot.slot_id, ct);
            }

            var before = SlotSnapshot(slot);
            slot.zone_id = request.ZoneId;
            slot.latitude = request.Latitude;
            slot.longitude = request.Longitude;
            slot.width_meters = request.WidthMeters;
            slot.length_meters = request.LengthMeters;
            slot.has_power = request.HasPower;
            slot.has_water = request.HasWater;
            slot.has_trash_bin = request.HasTrashBin;
            slot.business_category = request.BusinessCategory;
            Audit(actor, "SLOT_UPDATED", SlotEntity, slot.slot_id, new
            {
                before,
                after = SlotSnapshot(slot),
                warningsAcknowledged = AcknowledgedWarnings(check),
                reason = request.WarningReason,
            });
        }, ct);

        return new SlotMutationResultDto(await GetSlotDtoAsync(actor, slotId, ct), check);
    }

    public async Task<WardSlotDto> SetSlotStatusAsync(WardActor actor, long slotId, SetSlotStatusRequest request, CancellationToken ct)
    {
        await Write(async () =>
        {
            var slot = await RequireSlotAsync(actor, slotId, ct);
            EnsureToken(SlotToken(slot), request.VersionToken);
            if (slot.slot_status is not (SlotStatuses.Available or SlotStatuses.Suspended) || slot.slot_status == request.Status)
                throw new WardException(400, "invalid_status_transition", "Chỉ chuyển được giữa trạng thái Trống (AVAILABLE) và Tạm ngưng (SUSPENDED).");

            // A slot suspended by a permit action (WARD-11) still has its contract: reopening or
            // re-suspending it here would bypass that decision.
            if (await db.RentalContracts.AnyAsync(c => c.slot_id == slotId
                    && (c.contract_status == ContractStatuses.Active || c.contract_status == ContractStatuses.Suspended), ct))
                throw new WardException(400, "slot_has_contract", "Ô đang có hợp đồng, trạng thái được xử lý qua đình chỉ/thu hồi giấy phép (WARD-11).");
            if (await db.RentalApplications.AnyAsync(a => a.slot_id == slotId && ApplicationStatuses.Open.Contains(a.application_status), ct))
                throw new WardException(400, "slot_has_open_application", "Ô đang có đơn thuê chờ xử lý, hãy xử lý đơn trước.");

            var before = slot.slot_status;
            slot.slot_status = request.Status;
            Audit(actor, "SLOT_STATUS_CHANGED", SlotEntity, slot.slot_id,
                new { before = new { status = before }, after = new { status = slot.slot_status }, reason = request.Reason.Trim() });
        }, ct);
        return await GetSlotDtoAsync(actor, slotId, ct);
    }

    public async Task DeleteSlotAsync(WardActor actor, long slotId, string versionToken, CancellationToken ct)
    {
        await Write(async () =>
        {
            var slot = await RequireSlotAsync(actor, slotId, ct);
            EnsureToken(SlotToken(slot), versionToken);
            if (slot.source != SlotSources.WardDefined || slot.slot_status != SlotStatuses.Available
                || await SlotsWithHistory([slotId]).AnyAsync(ct))
                throw new WardException(400, "slot_has_history",
                    "Chỉ xóa được ô trống chưa từng phát sinh đơn, hợp đồng, vi phạm hay phản ánh. Hãy chuyển ô sang Tạm ngưng thay vì xóa.");

            var before = SlotSnapshot(slot);
            db.SidewalkSlots.Remove(slot);
            Audit(actor, "SLOT_DELETED", SlotEntity, slotId, new { before, after = (object?)null });
        }, ct);
    }

    public async Task<BatchPreviewDto> PreviewBatchAsync(WardActor actor, BatchPreviewRequest request, CancellationToken ct)
    {
        var zone = await RequireZoneAsync(actor, request.ZoneId, ct);
        var positions = SlotLine.Positions(request);
        var codes = await NextSlotCodesAsync(zone, positions.Count, ct);
        var context = await LoadPlacementContextAsync(actor, ct);
        var verified = true;

        var candidates = positions.Select((p, i) =>
        {
            var check = Evaluate(actor, new SlotPlacementInput(zone.zone_id, p.Latitude, p.Longitude, request.WidthMeters, request.LengthMeters), null, context);
            verified &= check.BoundaryVerified;
            return new BatchCandidateDto(i, codes[i], p.Latitude, p.Longitude, check.Issues);
        }).ToList();
        return new BatchPreviewDto(verified, candidates);
    }

    public async Task<IReadOnlyList<WardSlotDto>> CreateBatchAsync(WardActor actor, BatchCreateRequest request, CancellationToken ct)
    {
        var ids = new List<long>();
        await Write(async () =>
        {
            var zone = await RequireZoneAsync(actor, request.ZoneId, ct);
            var context = await LoadPlacementContextAsync(actor, ct);
            var checks = request.Positions
                .Select(p => Evaluate(actor, new SlotPlacementInput(zone.zone_id, p.Latitude, p.Longitude, request.WidthMeters, request.LengthMeters), null, context))
                .ToList();

            var blocked = checks.Select((c, i) => (c, i)).Where(x => x.c.Issues.Any(IsBlock)).Select(x => x.i + 1).ToList();
            if (blocked.Count > 0)
                throw new WardException(400, "placement_blocked", $"Các vị trí số {string.Join(", ", blocked)} bị chặn, hãy bỏ chúng khỏi danh sách.");
            if (!request.AcknowledgeWarnings && checks.Any(c => c.Issues.Count > 0))
                throw new WardException(400, "placement_warnings", "Có vị trí đang có cảnh báo. Hãy xem lại và xác nhận kèm lý do.");

            var codes = await NextSlotCodesAsync(zone, request.Positions.Count, ct);
            var slots = request.Positions.Select((p, i) => new SidewalkSlot
            {
                slot_code = codes[i],
                zone_id = zone.zone_id,
                latitude = p.Latitude,
                longitude = p.Longitude,
                width_meters = request.WidthMeters,
                length_meters = request.LengthMeters,
                slot_status = SlotStatuses.Available,
                source = SlotSources.WardDefined,
                has_power = request.HasPower,
                has_water = request.HasWater,
                has_trash_bin = request.HasTrashBin,
                business_category = request.BusinessCategory,
                created_at = Now,
            }).ToList();
            db.SidewalkSlots.AddRange(slots);
            await db.SaveChangesAsync(ct);
            ids = slots.Select(s => s.slot_id).ToList();

            Audit(actor, "SLOT_BATCH_CREATED", ZoneEntity, zone.zone_id, new
            {
                before = (object?)null,
                after = slots.Select(SlotSnapshot).ToList(),
                boundaryVerified = checks.All(c => c.BoundaryVerified),
                warningsAcknowledged = checks.SelectMany(AcknowledgedWarnings).Distinct().ToList(),
                reason = request.WarningReason,
            });
        }, ct);

        var created = await GridSlotsQuery(actor).Where(s => ids.Contains(s.slot_id)).OrderBy(s => s.slot_code).ToListAsync(ct);
        return await ToSlotDtosAsync(created, ct);
    }

    public async Task<IReadOnlyList<ConfigHistoryEntryDto>> ListSlotHistoryAsync(WardActor actor, long slotId, CancellationToken ct)
    {
        // A deleted slot no longer exists, so ownership is checked against the slot row while it
        // lives. A slot created via "rải hàng loạt" is audited once for the whole batch under its
        // zone (SLOT_BATCH_CREATED, see CreateBatchAsync) rather than per slot, so that creation
        // event surfaces on the zone's history, not here.
        if (!await GridSlotsQuery(actor).AnyAsync(s => s.slot_id == slotId, ct))
            throw SlotNotFound();

        var entries = await db.AuditLogs.AsNoTracking()
            .Where(a => a.entity_type == SlotEntity && a.entity_id == slotId)
            .OrderByDescending(a => a.created_at).ThenByDescending(a => a.audit_id)
            .Select(a => new { a.audit_id, a.action, a.actor_user.full_name, a.created_at, a.details })
            .ToListAsync(ct);
        return entries.Select(a => new ConfigHistoryEntryDto(a.audit_id, a.action, a.full_name ?? "Cán bộ phường",
            DateTime.SpecifyKind(a.created_at, DateTimeKind.Utc), a.details)).ToList();
    }

    public async Task<StreetFeatureMutationResultDto> CreateFeatureAsync(WardActor actor, UpsertStreetFeatureRequest request, CancellationToken ct)
    {
        var featureId = 0;
        await Write(async () =>
        {
            await RequireZoneAsync(actor, request.ZoneId, ct);
            var feature = new StreetFeature();
            ApplyFeatureFields(feature, request);
            db.StreetFeatures.Add(feature);
            await db.SaveChangesAsync(ct);
            featureId = feature.feature_id;
            Audit(actor, "STREET_FEATURE_CREATED", FeatureEntity, feature.feature_id,
                new { before = (object?)null, after = FeatureSnapshot(feature) });
        }, ct);
        return await FeatureResultAsync(actor, featureId, ct);
    }

    public async Task<StreetFeatureMutationResultDto> UpdateFeatureAsync(WardActor actor, int featureId, UpsertStreetFeatureRequest request, CancellationToken ct)
    {
        await Write(async () =>
        {
            var feature = await RequireFeatureAsync(actor, featureId, ct);
            EnsureToken(FeatureToken(feature), request.VersionToken);
            await RequireZoneAsync(actor, request.ZoneId, ct);
            var before = FeatureSnapshot(feature);
            ApplyFeatureFields(feature, request);
            Audit(actor, "STREET_FEATURE_UPDATED", FeatureEntity, feature.feature_id, new { before, after = FeatureSnapshot(feature) });
        }, ct);
        return await FeatureResultAsync(actor, featureId, ct);
    }

    public async Task DeleteFeatureAsync(WardActor actor, int featureId, string versionToken, CancellationToken ct)
    {
        await Write(async () =>
        {
            var feature = await RequireFeatureAsync(actor, featureId, ct);
            EnsureToken(FeatureToken(feature), versionToken);
            var before = FeatureSnapshot(feature);
            db.StreetFeatures.Remove(feature);
            Audit(actor, "STREET_FEATURE_DELETED", FeatureEntity, featureId, new { before, after = (object?)null });
        }, ct);
    }

    private IQueryable<SidewalkSlot> GridSlotsQuery(WardActor actor) =>
        db.SidewalkSlots.AsNoTracking()
            .Include(s => s.zone)
            .Where(s => s.zone.ward_unit_id == actor.WardId
                        && (s.source == SlotSources.WardDefined || s.proposal_review_status == ProposalReviewStatuses.Approved));

    private IQueryable<long> SlotsWithHistory(List<long> slotIds) =>
        db.SidewalkSlots.AsNoTracking()
            .Where(s => slotIds.Contains(s.slot_id)
                        && (s.RentalApplications.Any() || s.RentalContracts.Any() || s.Violations.Any()
                            || s.SlotHold != null || s.VendorReports.Any() || s.AddressChangeRequests.Any()))
            .Select(s => s.slot_id);

    private async Task<List<WardSlotDto>> ToSlotDtosAsync(List<SidewalkSlot> slots, CancellationToken ct)
    {
        var ids = slots.Select(s => s.slot_id).ToList();
        var withHistory = (await SlotsWithHistory(ids).ToListAsync(ct)).ToHashSet();
        return slots.Select(s => ToSlotDto(s, withHistory.Contains(s.slot_id))).ToList();
    }

    private async Task<WardSlotDto> GetSlotDtoAsync(WardActor actor, long slotId, CancellationToken ct)
    {
        var slot = await GridSlotsQuery(actor).SingleOrDefaultAsync(s => s.slot_id == slotId, ct) ?? throw SlotNotFound();
        return (await ToSlotDtosAsync([slot], ct))[0];
    }

    private static WardSlotDto ToSlotDto(SidewalkSlot s, bool hasHistory)
    {
        var editable = s.source == SlotSources.WardDefined && s.slot_status == SlotStatuses.Available;
        return new WardSlotDto(
            s.slot_id, s.slot_code, s.zone_id, s.zone.zone_name, s.latitude, s.longitude, s.width_meters, s.length_meters,
            s.slot_status, s.source, s.has_power, s.has_water, s.has_trash_bin, s.business_category,
            editable && !hasHistory, editable, SlotToken(s));
    }

    private WardStreetFeatureDto ToFeatureDto(StreetFeature f) =>
        new(f.feature_id, f.zone_id, f.feature_type, f.label, f.latitude, f.longitude, f.blocks_business, f.note,
            Settings.FeatureClearanceEnabled && Settings.FeatureClearanceMeters.TryGetValue(f.feature_type, out var c) ? c : null,
            FeatureToken(f));

    private async Task<PricingZone> RequireZoneAsync(WardActor actor, int zoneId, CancellationToken ct) =>
        await db.PricingZones.AsNoTracking().SingleOrDefaultAsync(z => z.zone_id == zoneId && z.ward_unit_id == actor.WardId, ct)
        ?? throw ZoneNotFound();

    private async Task<SidewalkSlot> RequireSlotAsync(WardActor actor, long slotId, CancellationToken ct) =>
        await db.SidewalkSlots.SingleOrDefaultAsync(s => s.slot_id == slotId && s.zone.ward_unit_id == actor.WardId
                                                          && (s.source == SlotSources.WardDefined
                                                              || s.proposal_review_status == ProposalReviewStatuses.Approved), ct)
        ?? throw SlotNotFound();

    private async Task<StreetFeature> RequireFeatureAsync(WardActor actor, int featureId, CancellationToken ct) =>
        await db.StreetFeatures.SingleOrDefaultAsync(f => f.feature_id == featureId && f.zone.ward_unit_id == actor.WardId, ct)
        ?? throw new NotFoundException("Không tìm thấy chướng ngại vật tại địa bàn phường của bạn.");

    private async Task<StreetFeatureMutationResultDto> FeatureResultAsync(WardActor actor, int featureId, CancellationToken ct)
    {
        var feature = await db.StreetFeatures.AsNoTracking().SingleAsync(f => f.feature_id == featureId, ct);
        var context = await LoadPlacementContextAsync(actor, ct);
        var affected = new List<PlacementIssue>();
        foreach (var slot in context.Slots)
        {
            var issue = FeatureIssue(feature, slot.Latitude, slot.Longitude, slot.HalfDiagonal)
                .FirstOrDefault();
            if (issue is not null)
                affected.Add(issue with { SlotId = slot.SlotId, Message = $"Ô {slot.Code}: {issue.Message}" });
        }
        return new StreetFeatureMutationResultDto(ToFeatureDto(feature), affected);
    }

    private static void ApplyFeatureFields(StreetFeature f, UpsertStreetFeatureRequest r)
    {
        f.zone_id = r.ZoneId;
        f.feature_type = r.FeatureType;
        f.label = r.Label.Trim();
        f.latitude = r.Latitude;
        f.longitude = r.Longitude;
        f.blocks_business = r.BlocksBusiness;
        f.note = NullIfBlank(r.Note);
    }

    private sealed record PlacedSlot(long SlotId, string Code, double Latitude, double Longitude, double Radius, double HalfDiagonal);

    private sealed record PlacementContext(List<PlacedSlot> Slots, List<StreetFeature> Features);

    private async Task<PlacementContext> LoadPlacementContextAsync(WardActor actor, CancellationToken ct)
    {
        var slots = await GridSlotsQuery(actor)
            .Select(s => new { s.slot_id, s.slot_code, s.latitude, s.longitude, s.width_meters, s.length_meters })
            .ToListAsync(ct);
        var features = await db.StreetFeatures.AsNoTracking()
            .Where(f => f.zone.ward_unit_id == actor.WardId)
            .ToListAsync(ct);
        return new PlacementContext(
            slots.Select(s =>
            {
                var w = (double)(s.width_meters ?? 0);
                var l = (double)(s.length_meters ?? 0);
                return new PlacedSlot(s.slot_id, s.slot_code, (double)s.latitude, (double)s.longitude,
                    Math.Max(w, l) / 2, Math.Sqrt(w * w + l * l) / 2);
            }).ToList(),
            features);
    }

    /// <summary>
    /// A slot is a point plus width/length with no stored orientation, so its footprint is
    /// approximated by circles: the half-diagonal (conservative) for features, and half the longer
    /// side for slot-to-slot overlap so a normal row of slots is not flagged.
    /// </summary>
    private PlacementCheckDto Evaluate(WardActor actor, SlotPlacementInput input, long? ignoreSlotId, PlacementContext context)
    {
        var issues = new List<PlacementIssue>();
        var lat = (double)input.Latitude;
        var lng = (double)input.Longitude;
        var w = (double)input.WidthMeters;
        var l = (double)input.LengthMeters;
        var halfDiagonal = Math.Sqrt(w * w + l * l) / 2;
        var radius = Math.Max(w, l) / 2;

        var boundaryVerified = true;
        try
        {
            if (!geolocation.Verify(actor.WardId, new GeoPoint(lat, lng)).Inside)
                issues.Add(new PlacementIssue(PlacementSeverities.Block, "outside_boundary",
                    "Vị trí nằm ngoài ranh giới quản lý của phường.", null, null, null));
        }
        catch (WardException ex) when (ex.Code == "boundary_unavailable")
        {
            boundaryVerified = false;
        }

        foreach (var feature in context.Features)
            issues.AddRange(FeatureIssue(feature, lat, lng, halfDiagonal));

        foreach (var other in context.Slots.Where(s => s.SlotId != ignoreSlotId))
        {
            var d = GeoMath.DistanceMeters(lat, lng, other.Latitude, other.Longitude);
            if (d < radius + other.Radius)
                issues.Add(new PlacementIssue(PlacementSeverities.Warn, "slot_overlap",
                    $"Có thể chồng lấn ô {other.Code} (cách {d:0.0} m).", null, other.SlotId, Math.Round(d, 1)));
        }

        return new PlacementCheckDto(boundaryVerified, issues);
    }

    private IEnumerable<PlacementIssue> FeatureIssue(StreetFeature feature, double lat, double lng, double halfDiagonal)
    {
        var d = GeoMath.DistanceMeters(lat, lng, (double)feature.latitude, (double)feature.longitude);
        var rounded = Math.Round(d, 1);
        if (d < halfDiagonal)
        {
            yield return feature.blocks_business
                ? new PlacementIssue(PlacementSeverities.Block, "feature_blocks_business",
                    $"Ô đè lên {feature.label} — vị trí phường đã đánh dấu cấm kinh doanh.", feature.feature_id, null, rounded)
                : new PlacementIssue(PlacementSeverities.Warn, "feature_overlap",
                    $"Ô đè lên {feature.label}.", feature.feature_id, null, rounded);
            yield break;
        }

        if (Settings.FeatureClearanceEnabled
            && Settings.FeatureClearanceMeters.TryGetValue(feature.feature_type, out var clearance)
            && d < halfDiagonal + clearance)
        {
            yield return new PlacementIssue(PlacementSeverities.Warn, "feature_clearance",
                $"Cách {feature.label} khoảng {Math.Max(0, d - halfDiagonal):0.0} m, dưới khoảng cách cấu hình {clearance:0.#} m.",
                feature.feature_id, null, rounded);
        }
    }

    private static bool IsBlock(PlacementIssue issue) => issue.Severity == PlacementSeverities.Block;

    private static void EnsurePlacementAccepted(PlacementCheckDto check, bool acknowledgeWarnings)
    {
        var blocks = check.Issues.Where(IsBlock).ToList();
        if (blocks.Count > 0)
            throw new WardException(400, "placement_blocked", string.Join(" ", blocks.Select(b => b.Message)));
        if (!acknowledgeWarnings && check.Issues.Count > 0)
            throw new WardException(400, "placement_warnings", "Vị trí có cảnh báo. Hãy xem lại và xác nhận kèm lý do.");
    }

    private static List<string> AcknowledgedWarnings(PlacementCheckDto check) =>
        check.Issues.Where(i => !IsBlock(i)).Select(i => i.Code).Distinct().ToList();

    private bool BoundaryConfigured(int wardId)
    {
        try
        {
            geolocation.Verify(wardId, new GeoPoint(0, 0));
            return true;
        }
        catch (WardException ex) when (ex.Code == "boundary_unavailable")
        {
            return false;
        }
        catch (WardException)
        {
            return true;
        }
    }

    private async Task<string> RequireFreeSlotCodeAsync(string code, long? selfId, CancellationToken ct)
    {
        if (await db.SidewalkSlots.AnyAsync(s => s.slot_code == code && s.slot_id != selfId, ct))
            throw new WardException(400, "slot_code_taken", $"Mã ô {code} đã tồn tại.");
        return code;
    }

    private async Task<List<string>> NextSlotCodesAsync(PricingZone zone, int count, CancellationToken ct)
    {
        var prefix = (string.IsNullOrWhiteSpace(zone.zone_code) ? $"Z{zone.zone_id}" : zone.zone_code) + "-";
        var existing = await db.SidewalkSlots.AsNoTracking()
            .Where(s => s.slot_code.StartsWith(prefix))
            .Select(s => s.slot_code)
            .ToListAsync(ct);
        var taken = existing.ToHashSet(StringComparer.Ordinal);
        var next = existing
            .Select(c => int.TryParse(c[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        var codes = new List<string>(count);
        while (codes.Count < count)
        {
            var code = prefix + next.ToString("D3", CultureInfo.InvariantCulture);
            if (!taken.Contains(code)) codes.Add(code);
            next++;
        }
        return codes;
    }

    private static object SlotSnapshot(SidewalkSlot s) => new
    {
        slotCode = s.slot_code,
        zoneId = s.zone_id,
        latitude = s.latitude,
        longitude = s.longitude,
        widthMeters = s.width_meters,
        lengthMeters = s.length_meters,
        status = s.slot_status,
        hasPower = s.has_power,
        hasWater = s.has_water,
        hasTrashBin = s.has_trash_bin,
        businessCategory = s.business_category,
    };

    private static object FeatureSnapshot(StreetFeature f) => new
    {
        zoneId = f.zone_id,
        featureType = f.feature_type,
        label = f.label,
        latitude = f.latitude,
        longitude = f.longitude,
        blocksBusiness = f.blocks_business,
        note = f.note,
    };

    private static string SlotToken(SidewalkSlot s) =>
        VersionToken.Compute(s.slot_code, s.zone_id, s.latitude, s.longitude, s.width_meters, s.length_meters, s.slot_status,
            s.has_power, s.has_water, s.has_trash_bin, s.business_category);

    private static string FeatureToken(StreetFeature f) =>
        VersionToken.Compute(f.zone_id, f.feature_type, f.label, f.latitude, f.longitude, f.blocks_business, f.note);

    private static NotFoundException SlotNotFound() => new("Không tìm thấy ô sạp tại địa bàn phường của bạn.");
    #endregion

    #region WardCompliancePolicy (Phase A)
    public async Task<WardCompliancePolicyDto> GetCompliancePolicyAsync(WardActor actor, CancellationToken ct)
    {
        var policy = await db.WardCompliancePolicies.AsNoTracking()
            .Include(p => p.UserAccount)
            .SingleOrDefaultAsync(p => p.ward_unit_id == actor.WardId, ct);
        return ToCompliancePolicyDto(policy);
    }

    public async Task<WardCompliancePolicyDto> UpsertCompliancePolicyAsync(
        WardActor actor, UpsertWardCompliancePolicyRequest request, CancellationToken ct)
    {
        await Write(async () =>
        {
            var policy = await db.WardCompliancePolicies.SingleOrDefaultAsync(p => p.ward_unit_id == actor.WardId, ct);
            var before = policy is null ? null : ToCompliancePolicyDto(policy);
            if (policy is null)
            {
                policy = new WardCompliancePolicy { ward_unit_id = actor.WardId, ward_unit_type = "WARD" };
                db.WardCompliancePolicies.Add(policy);
            }

            policy.violation_threshold_count = request.ViolationThresholdCount;
            policy.violation_window_days = request.ViolationWindowDays;
            policy.unpaid_penalty_grace_days = request.UnpaidPenaltyGraceDays;
            policy.updated_by = actor.UserId;
            policy.updated_at = Now;

            Audit(actor, "COMPLIANCE_POLICY_UPDATED", "WardCompliancePolicy", actor.WardId,
                new { before, after = request });
        }, ct);
        return await GetCompliancePolicyAsync(actor, ct);
    }

    private static WardCompliancePolicyDto ToCompliancePolicyDto(WardCompliancePolicy? policy) =>
        policy is null
            ? new WardCompliancePolicyDto(null, null, null, null, null)
            : new WardCompliancePolicyDto(
                policy.violation_threshold_count, policy.violation_window_days, policy.unpaid_penalty_grace_days,
                policy.updated_at, policy.UserAccount?.full_name);
    #endregion

    #region Shared
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
        db.ChangeTracker.Clear();
    }

    private void Audit(WardActor actor, string action, string entityType, long entityId, object details) =>
        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actor.UserId,
            action = action,
            entity_type = entityType,
            entity_id = entityId,
            details = JsonSerializer.Serialize(details, AuditJson),
            created_at = Now,
        });

    private static void EnsureToken(string current, string? expected)
    {
        if (!string.Equals(current, expected, StringComparison.Ordinal))
            throw new ConflictException("Dữ liệu vừa được cán bộ khác cập nhật. Vui lòng tải lại trước khi lưu.");
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    #endregion
}

/// <summary>Optimistic-concurrency token over every field a write may change -- no rowversion column needed.</summary>
internal static class VersionToken
{
    public static string Compute(params object?[] parts)
    {
        var canonical = string.Join('\u001f', parts.Select(p => p switch
        {
            null => "␀",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => p.ToString(),
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>Evenly spaced slot centres along a straight kerb segment.</summary>
internal static class SlotLine
{
    public const int MaxSlots = 50;

    public static List<BatchSlotPosition> Positions(BatchPreviewRequest r)
    {
        var sLat = (double)r.StartLatitude;
        var sLng = (double)r.StartLongitude;
        var eLat = (double)r.EndLatitude;
        var eLng = (double)r.EndLongitude;
        var distance = GeoMath.DistanceMeters(sLat, sLng, eLat, eLng);
        var length = (double)r.LengthMeters;
        var step = length + (double)r.GapMeters;
        if (distance < length)
            throw new WardException(400, "segment_too_short", "Đoạn đã chọn ngắn hơn chiều dài một ô.");

        var count = Math.Min(MaxSlots, (int)Math.Floor((distance - length) / step) + 1);
        return Enumerable.Range(0, count).Select(i =>
        {
            var t = (length / 2 + i * step) / distance;
            return new BatchSlotPosition(
                Math.Round((decimal)(sLat + (eLat - sLat) * t), 6),
                Math.Round((decimal)(sLng + (eLng - sLng) * t), 6));
        }).ToList();
    }
}

/// <summary>A rate is in force on a day when effective_from &lt;= day &lt; effective_to.</summary>
public static class PenaltyRates
{
    public static bool IsInForce(DateOnly from, DateOnly? to, DateOnly day) => from <= day && (to is null || to > day);
}
