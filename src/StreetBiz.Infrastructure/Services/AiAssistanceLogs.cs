using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Features.AiAssistance;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Services;

/// <summary>
/// BR-41 logging core: every row this writes is one AI suggestion, and ReviewAsync is the only
/// way an officer's accept/reject gets recorded. See AiAssistanceLogs.Contracts for the envelope
/// format, which doubles as the per-feature cache (RunDocumentCheckAsync and WardAiInsights key
/// off it so a page reload does not re-bill the provider).
/// </summary>
public sealed class AiAssistanceLogs(StreetBizDbContext db, TimeProvider clock) : IAiAssistanceLogs
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Envelope(string? InputKey, JsonElement Output);

    public async Task<long> RecordAsync(
        string featureCode, string entityType, long entityId, string? inputKey,
        object output, decimal? confidence, CancellationToken ct)
    {
        var log = new AIAssistanceLog
        {
            feature_code = featureCode,
            entity_type = entityType,
            entity_id = entityId,
            ai_output = JsonSerializer.Serialize(new { inputKey, output }, JsonOptions),
            confidence = confidence,
            created_at = Now,
        };
        db.AIAssistanceLogs.Add(log);
        await db.SaveChangesAsync(ct);
        return log.ai_log_id;
    }

    public async Task<AiLogged<T>?> FindLatestAsync<T>(
        string featureCode, string entityType, long entityId, string? inputKey, CancellationToken ct)
    {
        var log = await db.AIAssistanceLogs.AsNoTracking()
            .Where(x => x.feature_code == featureCode && x.entity_type == entityType && x.entity_id == entityId)
            .OrderByDescending(x => x.created_at)
            .FirstOrDefaultAsync(ct);

        if (log is null)
        {
            return null;
        }

        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(log.ai_output, JsonOptions);
        }
        catch (JsonException)
        {
            // A row written before this envelope shape existed (db/StreetBiz_Demo_Seed.sql's
            // AIC-01 row) is a cache miss, not a crash.
            return null;
        }

        if (envelope is null || (inputKey is not null && envelope.InputKey != inputKey))
        {
            return null;
        }

        T output;
        try
        {
            output = envelope.Output.Deserialize<T>(JsonOptions)!;
        }
        catch (JsonException)
        {
            return null;
        }

        return new AiLogged<T>(log.ai_log_id, envelope.InputKey, output, log.created_at, log.accepted);
    }

    public async Task<AiSuggestionFeedbackDto> ReviewAsync(
        WardActor actor, long aiLogId, AiSuggestionFeedbackRequest request, CancellationToken ct)
    {
        var log = await db.AIAssistanceLogs.SingleOrDefaultAsync(x => x.ai_log_id == aiLogId, ct)
            ?? throw NotFound();

        if (!await InWardAsync(log.entity_type, log.entity_id, actor.WardId, ct))
        {
            throw NotFound();
        }

        log.reviewed_by = actor.UserId;
        log.accepted = request.Accepted;
        log.reviewed_at = Now;

        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actor.UserId,
            action = "AI_SUGGESTION_REVIEWED",
            entity_type = "AIAssistanceLog",
            entity_id = log.ai_log_id,
            details = JsonSerializer.Serialize(
                new
                {
                    featureCode = log.feature_code,
                    entityType = log.entity_type,
                    entityId = log.entity_id,
                    accepted = request.Accepted,
                    note = request.Note,
                },
                JsonOptions),
            created_at = Now,
        });

        await db.SaveChangesAsync(ct);
        return new AiSuggestionFeedbackDto(log.ai_log_id, request.Accepted, log.reviewed_at!.Value, actor.Name);
    }

    private static NotFoundException NotFound() => new("Không tìm thấy gợi ý AI tại địa bàn phường của bạn.");

    /// <summary>One case per AiLogEntities constant. An unrecognized entity_type returns false --
    /// the same outward 404 as a ward mismatch, so neither leaks which one happened.</summary>
    private Task<bool> InWardAsync(string entityType, long entityId, int wardId, CancellationToken ct) => entityType switch
    {
        AiLogEntities.Registration => db.BusinessRegistrations.AsNoTracking()
            .AnyAsync(x => x.registration_id == entityId && x.ward_unit_id == wardId, ct),

        AiLogEntities.Slot => db.SidewalkSlots.AsNoTracking()
            .AnyAsync(x => x.slot_id == entityId && x.zone.ward_unit_id == wardId, ct),

        AiLogEntities.Zone => db.PricingZones.AsNoTracking()
            .AnyAsync(x => x.zone_id == entityId && x.ward_unit_id == wardId, ct),

        AiLogEntities.Permit => db.DigitalPermits.AsNoTracking()
            .AnyAsync(x => x.permit_id == entityId && x.contract.slot.zone.ward_unit_id == wardId, ct),

        AiLogEntities.ScanLog => db.PermitScanLogs.AsNoTracking()
            .AnyAsync(x => x.scan_id == entityId && x.permit != null && x.permit.contract.slot.zone.ward_unit_id == wardId, ct),

        // Same rule as WardComplianceService.InWard: a violation belongs to its slot's ward, else
        // its contract's slot's ward, else the ward of the officer who recorded it (BR-45).
        AiLogEntities.Violation => db.Violations.AsNoTracking().AnyAsync(v =>
            v.violation_id == entityId
            && ((v.slot_id != null && v.slot!.zone.ward_unit_id == wardId)
                || (v.slot_id == null && v.contract_id != null && v.contract!.slot.zone.ward_unit_id == wardId)
                || (v.slot_id == null && v.contract_id == null && v.UserAccount!.ward_unit_id == wardId)),
            ct),

        AiLogEntities.Ward => Task.FromResult(entityId == wardId),

        _ => Task.FromResult(false),
    };
}
