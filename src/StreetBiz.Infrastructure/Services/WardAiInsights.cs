using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.AiAssistance;
using StreetBiz.Application.Features.WardCompliance;
using StreetBiz.Application.Features.WardConfiguration;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Services;

/// <summary>
/// Orchestrates AIC-04 (proposed-slot feasibility), AIC-06 (geofence drift) and AIC-07 (zone
/// price suggestion), plus the ward-scoped wrapper around AIC-02 (encroachment check) that
/// GetAiEncroachmentCheckQueryHandler calls. The deterministic decisions live in AiInsightRules;
/// this class only loads ward-scoped data, calls the AI provider for the parts that need one,
/// and logs through IAiAssistanceLogs (BR-41).
/// </summary>
public sealed class WardAiInsights(
    StreetBizDbContext db,
    IAiComplianceService aiService,
    IAiAssistanceLogs aiLogs,
    IWardConfigurationService wardConfig,
    TimeProvider clock)
    : IWardAiInsights
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static readonly TimeZoneInfo VietnamTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        "Asia/Ho_Chi_Minh", TimeSpan.FromHours(7), "Asia/Ho_Chi_Minh", "Asia/Ho_Chi_Minh");

    private DateOnly TodayVn =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(Now, DateTimeKind.Utc), VietnamTimeZone));

    #region AIC-04: proposed-slot feasibility
    public async Task<AiProposalAssessmentView> GetProposalAssessmentAsync(WardActor actor, long slotId, CancellationToken ct)
    {
        var slot = await ProposalSlotAsync(actor, slotId, ct);
        var input = await BuildProposalInputAsync(actor, slot, ct);
        var key = ProposalInputKey(input);

        var latest = await aiLogs.FindLatestAsync<AiProposalAssessment>(
            AiFeatureCodes.ProposalSite, AiLogEntities.Slot, slot.slot_id, inputKey: null, ct);

        if (latest is null)
        {
            return new AiProposalAssessmentView(null, IsStale: false);
        }

        var result = latest.Output with { AiLogId = latest.AiLogId, AssessedAt = latest.CreatedAt };
        return new AiProposalAssessmentView(result, IsStale: latest.InputKey != key);
    }

    public async Task<AiProposalAssessment> RunProposalAssessmentAsync(WardActor actor, long slotId, CancellationToken ct)
    {
        var slot = await ProposalSlotAsync(actor, slotId, ct);
        if (slot.proposal_review_status != ProposalReviewStatuses.Pending)
        {
            throw new WardException(409, "review_conflict", "Đề xuất này đã được xử lý, không thể đánh giá lại.");
        }

        var input = await BuildProposalInputAsync(actor, slot, ct);
        var key = ProposalInputKey(input);

        // Same proposal, same rule-check result, same photo: reuse the logged answer instead of
        // spending another model call -- this doubles as the GET endpoint's cache.
        var cached = await aiLogs.FindLatestAsync<AiProposalAssessment>(
            AiFeatureCodes.ProposalSite, AiLogEntities.Slot, slot.slot_id, key, ct);
        if (cached is not null)
        {
            return cached.Output with { AiLogId = cached.AiLogId, AssessedAt = cached.CreatedAt };
        }

        var result = await aiService.AssessProposalSiteAsync(input, ct);
        if (!result.IsAiGenerated)
        {
            // A rule-only fallback is not "AI output" -- BR-41's log is for real answers.
            return result;
        }

        var logId = await aiLogs.RecordAsync(
            AiFeatureCodes.ProposalSite, AiLogEntities.Slot, slot.slot_id, key, result, result.Confidence, ct);
        return result with { AiLogId = logId, AssessedAt = Now };
    }

    private async Task<SidewalkSlot> ProposalSlotAsync(WardActor actor, long slotId, CancellationToken ct)
    {
        var slot = await db.SidewalkSlots.AsNoTracking()
            .Include(x => x.zone)
            .SingleOrDefaultAsync(x =>
                x.slot_id == slotId && x.source == SlotSources.VendorProposed && x.zone.ward_unit_id == actor.WardId, ct);

        return slot ?? throw new NotFoundException("Không tìm thấy đề xuất ô tại địa bàn phường của bạn.");
    }

    private async Task<AiProposalSiteInput> BuildProposalInputAsync(WardActor actor, SidewalkSlot slot, CancellationToken ct)
    {
        var placementInput = new SlotPlacementInput(
            slot.zone_id, slot.latitude, slot.longitude, slot.width_meters ?? 0m, slot.length_meters ?? 0m);
        var check = await wardConfig.CheckPlacementAsync(actor, placementInput, ignoreSlotId: slot.slot_id, ct);

        return new AiProposalSiteInput(
            slot.slot_id,
            (double)slot.latitude,
            (double)slot.longitude,
            slot.width_meters,
            slot.length_meters,
            slot.zone.zone_code ?? slot.zone.zone_name,
            slot.zone.available_from,
            slot.zone.available_to,
            slot.proposal_photo_url,
            check.BoundaryVerified,
            check.Issues);
    }

    /// <summary>Fingerprint of everything the assessment reads -- a changed photo, geometry or
    /// rule result (e.g. a StreetFeature added nearby) invalidates the cached answer.</summary>
    private static string ProposalInputKey(AiProposalSiteInput input)
    {
        var issues = string.Join(',', input.RuleChecks.Select(i => i.Code).OrderBy(c => c, StringComparer.Ordinal));
        var raw = string.Join(
            '|',
            input.Latitude.ToString("F6", CultureInfo.InvariantCulture),
            input.Longitude.ToString("F6", CultureInfo.InvariantCulture),
            input.WidthMeters,
            input.LengthMeters,
            input.ProposalPhotoUrl,
            input.ZoneName,
            issues);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
    #endregion

    #region AIC-06: geofence drift
    public async Task<GeofenceDriftReportDto> GetGeofenceDriftAsync(WardActor actor, CancellationToken ct)
    {
        var since = Now.AddDays(-GeofenceDriftDefaults.WindowDays);

        // A buyer's BUY-02 scan is location evidence too, not just an officer's WARD_INSPECTION --
        // both carry scan_context through to the officer so they can weigh it themselves.
        var scans = await db.PermitScanLogs.AsNoTracking()
            .Where(s => s.permit_id != null && s.latitude != null && s.longitude != null && s.scanned_at >= since)
            .Where(s => s.permit!.permit_status != PermitStatuses.Revoked)
            .Where(s => s.permit!.contract.contract_status == ContractStatuses.Active
                || s.permit!.contract.contract_status == ContractStatuses.Suspended)
            .Where(s => s.permit!.contract.slot.zone.ward_unit_id == actor.WardId)
            .Select(s => new
            {
                s.scan_id,
                PermitId = s.permit_id!.Value,
                s.scanned_at,
                s.scan_context,
                Latitude = (double)s.latitude!.Value,
                Longitude = (double)s.longitude!.Value,
                ContractId = s.permit!.contract_id,
                SlotId = s.permit.contract.slot_id,
                SlotCode = s.permit.contract.slot.slot_code,
                SlotLatitude = (double)s.permit.contract.slot.latitude,
                SlotLongitude = (double)s.permit.contract.slot.longitude,
                ZoneName = s.permit.contract.slot.zone.zone_code ?? s.permit.contract.slot.zone.zone_name,
                VendorId = s.permit.contract.vendor_id,
                VendorName = s.permit.contract.vendor.BusinessRegistrations
                    .OrderByDescending(r => r.registration_id)
                    .Select(r => r.display_name)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        if (scans.Count == 0)
        {
            return new GeofenceDriftReportDto(GeofenceDriftDefaults.WindowDays, GeofenceDriftDefaults.ToleranceMeters, []);
        }

        var items = new List<GeofenceDriftItemDto>();
        var toExplain = new List<(long PermitId, GeofenceDriftFacts Facts, string InputKey)>();

        foreach (var group in scans.GroupBy(x => x.PermitId))
        {
            var rows = group.ToList();
            var first = rows[0];
            var driftScans = rows
                .Select(r => new AiInsightRules.DriftScan(r.scan_id, r.scanned_at, r.scan_context, r.Latitude, r.Longitude))
                .ToList();

            var evaluation = AiInsightRules.EvaluateDrift(first.SlotLatitude, first.SlotLongitude, driftScans);
            if (evaluation is null)
            {
                continue; // every scan for this permit was within tolerance
            }

            var scansDto = evaluation.ScansWithDistance
                .OrderByDescending(x => x.Scan.ScannedAt)
                .Select(x => new GeofenceDriftScanDto(
                    x.Scan.ScanId, x.Scan.ScannedAt, x.Scan.ScanContext, Math.Round(x.DistanceMeters, 1), x.Scan.Latitude, x.Scan.Longitude))
                .ToList();

            var explanation = AiInsightRules.DriftExplanation(
                evaluation.OffSiteCount, evaluation.ScanCount, evaluation.Pattern, evaluation.MeanOffsetMeters, evaluation.MeanOffsetBearingDegrees);
            long? aiLogId = null;
            var isAiGenerated = false;

            if (evaluation.Level == DriftLevels.Drift)
            {
                var inputKey = DriftInputKey(group.Key, rows.Select(r => r.scan_id));
                var cached = await aiLogs.FindLatestAsync<string>(AiFeatureCodes.GeofenceDrift, AiLogEntities.Permit, group.Key, inputKey, ct);
                if (cached is not null)
                {
                    explanation = cached.Output;
                    aiLogId = cached.AiLogId;
                    isAiGenerated = true;
                }
                else
                {
                    toExplain.Add((group.Key, new GeofenceDriftFacts(
                        group.Key, first.SlotCode, first.ZoneName, evaluation.ScanCount, evaluation.OffSiteCount,
                        Math.Round(evaluation.MaxDistanceMeters, 1), evaluation.MeanOffsetMeters, evaluation.MeanOffsetBearingDegrees,
                        evaluation.Pattern), inputKey));
                }
            }

            items.Add(new GeofenceDriftItemDto(
                group.Key, first.ContractId, first.SlotId, first.SlotCode, first.ZoneName,
                first.VendorName ?? $"Hộ kinh doanh #{first.VendorId}",
                evaluation.ScanCount, evaluation.OffSiteCount, Math.Round(evaluation.MaxDistanceMeters, 1),
                evaluation.MeanOffsetMeters.HasValue ? Math.Round(evaluation.MeanOffsetMeters.Value, 1) : null,
                evaluation.MeanOffsetBearingDegrees, evaluation.Level, evaluation.Pattern,
                scansDto.Max(s => s.ScannedAt), scansDto, explanation, isAiGenerated, aiLogId));
        }

        if (toExplain.Count > 0)
        {
            // One batched call for every newly-flagged permit, not one per permit.
            var explained = await aiService.ExplainGeofenceDriftAsync(toExplain.Select(x => x.Facts).ToList(), ct);
            if (explained is not null)
            {
                var byPermit = toExplain.ToDictionary(x => x.PermitId);
                for (var i = 0; i < items.Count; i++)
                {
                    if (!byPermit.TryGetValue(items[i].PermitId, out var pending)
                        || !explained.TryGetValue(pending.PermitId, out var text)
                        || string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var logId = await aiLogs.RecordAsync(
                        AiFeatureCodes.GeofenceDrift, AiLogEntities.Permit, pending.PermitId, pending.InputKey, text, confidence: null, ct);
                    items[i] = items[i] with { Explanation = $"[AI] {text}", IsAiGenerated = true, AiLogId = logId };
                }
            }
        }

        var ordered = items
            .OrderByDescending(x => x.Level == DriftLevels.Drift)
            .ThenByDescending(x => x.MaxDistanceMeters)
            .ToList();
        return new GeofenceDriftReportDto(GeofenceDriftDefaults.WindowDays, GeofenceDriftDefaults.ToleranceMeters, ordered);
    }

    private static string DriftInputKey(long permitId, IEnumerable<long> scanIds)
    {
        var raw = $"{permitId}|{string.Join(',', scanIds.OrderBy(x => x))}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
    #endregion

    #region AIC-07: zone price suggestion
    private const int PriceWindowDays = 90;

    public async Task<ZonePriceSuggestionDto> GetZonePriceSuggestionAsync(WardActor actor, int zoneId, CancellationToken ct)
    {
        var zone = await db.PricingZones.AsNoTracking()
            .SingleOrDefaultAsync(z => z.zone_id == zoneId && z.ward_unit_id == actor.WardId, ct)
            ?? throw new NotFoundException("Không tìm thấy tuyến tại địa bàn phường của bạn.");

        var zoneName = zone.zone_code ?? zone.zone_name;

        // The grid's own slots, same definition as WARD-01's grid query: ward-placed, or a
        // vendor proposal already approved onto the grid.
        var slotIds = await db.SidewalkSlots.AsNoTracking()
            .Where(s => s.zone_id == zoneId
                && (s.source == SlotSources.WardDefined || s.proposal_review_status == ProposalReviewStatuses.Approved))
            .Select(s => s.slot_id)
            .ToListAsync(ct);

        var fromDate = TodayVn.AddDays(-PriceWindowDays);
        var toDate = TodayVn.AddDays(-1);

        var occupiedSlotDays = 0;
        if (slotIds.Count > 0)
        {
            var contracts = await db.RentalContracts.AsNoTracking()
                .Where(c => slotIds.Contains(c.slot_id) && c.contract_status != ContractStatuses.Revoked && c.start_date <= toDate)
                .Select(c => new { c.start_date, c.end_date, c.contract_status, c.cancelled_at })
                .ToListAsync(ct);

            // EXPIRED rows are not kept non-overlapping by the DB trigger (that only guards
            // ACTIVE/SUSPENDED), so a slot-day union, not a simple sum, is needed here too.
            occupiedSlotDays = contracts.Sum(c =>
            {
                var effectiveEnd = c.contract_status == ContractStatuses.Cancelled && c.cancelled_at.HasValue
                    ? DateOnly.FromDateTime(c.cancelled_at.Value)
                    : c.end_date;

                var rangeStart = c.start_date > fromDate ? c.start_date : fromDate;
                var rangeEnd = effectiveEnd < toDate ? effectiveEnd : toDate;
                return Math.Max(0, rangeEnd.DayNumber - rangeStart.DayNumber + 1);
            });
        }

        var availableSlotDays = slotIds.Count * PriceWindowDays;
        var occupancyPercent = availableSlotDays == 0 ? 0d : 100d * occupiedSlotDays / availableSlotDays;

        var sinceUtc = Now.AddDays(-PriceWindowDays);
        var applicationStatuses = slotIds.Count == 0
            ? new List<string>()
            : await db.RentalApplications.AsNoTracking()
                .Where(a => slotIds.Contains(a.slot_id) && a.created_at >= sinceUtc)
                .Select(a => a.application_status)
                .ToListAsync(ct);

        var activeHolds = slotIds.Count == 0
            ? 0
            : await db.SlotHolds.AsNoTracking().CountAsync(h => slotIds.Contains(h.slot_id) && h.expires_at > Now, ct);

        var pendingCount = applicationStatuses.Count(s => s is ApplicationStatuses.Pending or ApplicationStatuses.UnderReview);
        var rejectedCount = applicationStatuses.Count(s => s == ApplicationStatuses.Rejected);

        var baseline = AiInsightRules.PriceBaseline(zone.price_per_day, slotIds.Count, occupancyPercent, pendingCount);

        if (baseline.Direction == PriceDirections.InsufficientData)
        {
            return new ZonePriceSuggestionDto(
                zoneId, zoneName, zone.price_per_day, PriceWindowDays, 0, 0, 0, 0,
                applicationStatuses.Count, rejectedCount, pendingCount, activeHolds,
                baseline.Direction, baseline.BaselinePricePerDay, baseline.BaselinePricePerDay,
                baseline.MinAllowedPricePerDay, baseline.MaxAllowedPricePerDay,
                "[Hệ thống — chưa xác minh bằng AI] Tuyến chưa có ô nào để tính tỉ lệ lấp đầy.",
                IsAiGenerated: false, AiLogId: null);
        }

        var inputKey = ZonePriceInputKey(
            zoneId, zone.price_per_day, occupiedSlotDays, availableSlotDays,
            applicationStatuses.Count, rejectedCount, pendingCount, activeHolds, TodayVn);

        var cached = await aiLogs.FindLatestAsync<ZonePriceSuggestionDto>(AiFeatureCodes.ZonePrice, AiLogEntities.Zone, zoneId, inputKey, ct);
        if (cached is not null)
        {
            return cached.Output with { AiLogId = cached.AiLogId };
        }

        var facts = new ZonePriceFacts(
            zoneId, zoneName, zone.price_per_day, PriceWindowDays, slotIds.Count, occupiedSlotDays, availableSlotDays,
            Math.Round(occupancyPercent, 1), applicationStatuses.Count, rejectedCount, pendingCount, activeHolds,
            baseline.Direction, baseline.BaselinePricePerDay, baseline.MinAllowedPricePerDay, baseline.MaxAllowedPricePerDay,
            zone.available_from, zone.available_to);

        var advice = await aiService.AdviseZonePriceAsync(facts, ct);

        decimal suggested;
        string explanation;
        bool isAiGenerated;
        if (advice is { ProposedPricePerDay: not null })
        {
            suggested = AiInsightRules.ClampPrice(advice.ProposedPricePerDay.Value, baseline.MinAllowedPricePerDay, baseline.MaxAllowedPricePerDay);
            explanation = $"[AI] {advice.Explanation}";
            isAiGenerated = true;
        }
        else
        {
            suggested = baseline.BaselinePricePerDay;
            explanation = DirectionExplanation(baseline.Direction, occupancyPercent, pendingCount);
            isAiGenerated = false;
        }

        var result = new ZonePriceSuggestionDto(
            zoneId, zoneName, zone.price_per_day, PriceWindowDays, slotIds.Count, occupiedSlotDays, availableSlotDays,
            Math.Round(occupancyPercent, 1), applicationStatuses.Count, rejectedCount, pendingCount, activeHolds,
            baseline.Direction, baseline.BaselinePricePerDay, suggested,
            baseline.MinAllowedPricePerDay, baseline.MaxAllowedPricePerDay, explanation, isAiGenerated, AiLogId: null);

        if (!isAiGenerated)
        {
            return result;
        }

        var logId = await aiLogs.RecordAsync(AiFeatureCodes.ZonePrice, AiLogEntities.Zone, zoneId, inputKey, result, confidence: null, ct);
        return result with { AiLogId = logId };
    }

    private static string DirectionExplanation(string direction, double occupancyPercent, int pendingCount) => direction switch
    {
        PriceDirections.Raise =>
            $"[Hệ thống — chưa xác minh bằng AI] Tỉ lệ lấp đầy {occupancyPercent:0.#}% trong 90 ngày qua, đề nghị tăng giá.",
        PriceDirections.Lower =>
            $"[Hệ thống — chưa xác minh bằng AI] Tỉ lệ lấp đầy {occupancyPercent:0.#}%, ít hồ sơ đang chờ ({pendingCount}), đề nghị giảm giá.",
        _ => $"[Hệ thống — chưa xác minh bằng AI] Tỉ lệ lấp đầy {occupancyPercent:0.#}%, giữ nguyên giá hiện tại.",
    };

    private static string ZonePriceInputKey(
        int zoneId, decimal price, int occupiedSlotDays, int availableSlotDays,
        int applications, int rejected, int pending, int holds, DateOnly today)
    {
        var raw = string.Join(
            '|', zoneId, price, occupiedSlotDays, availableSlotDays, applications, rejected, pending, holds, today);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
    #endregion

    #region AIC-02 retrofit: ward-scoped encroachment check
    public async Task<AiEncroachmentResult> CheckEncroachmentAsync(
        WardActor actor, string photoUrl, double? slotWidth, double? slotLength, long? slotId, CancellationToken ct)
    {
        if (slotId is { } id)
        {
            var slot = await db.SidewalkSlots.AsNoTracking()
                .SingleOrDefaultAsync(s => s.slot_id == id && s.zone.ward_unit_id == actor.WardId, ct)
                ?? throw new NotFoundException("Không tìm thấy ô sạp tại địa bàn phường của bạn.");

            var result = await aiService.AnalyzeInspectionPhotoAsync(
                photoUrl,
                slot.width_meters.HasValue ? (double)slot.width_meters.Value : null,
                slot.length_meters.HasValue ? (double)slot.length_meters.Value : null,
                ct);

            return await LogEncroachmentAsync(result, AiLogEntities.Slot, id, ct);
        }

        var freeFormResult = await aiService.AnalyzeInspectionPhotoAsync(photoUrl, slotWidth, slotLength, ct);
        return await LogEncroachmentAsync(freeFormResult, AiLogEntities.Ward, actor.WardId, ct);
    }

    private async Task<AiEncroachmentResult> LogEncroachmentAsync(AiEncroachmentResult result, string entityType, long entityId, CancellationToken ct)
    {
        if (!result.IsAiGenerated)
        {
            return result;
        }

        var logId = await aiLogs.RecordAsync(AiFeatureCodes.InspectionPhoto, entityType, entityId, inputKey: null, result, confidence: null, ct);
        return result with { AiLogId = logId };
    }
    #endregion
}
