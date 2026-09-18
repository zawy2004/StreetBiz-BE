using StreetBiz.Application.Common.Exceptions;

namespace StreetBiz.Application.Features.WardSlots;

public sealed record WardActor(long UserId, int WardId, string Name);
public sealed record GeoPoint(double Latitude, double Longitude);
public sealed record GeocodeResult(string Label, GeoPoint Point);
public sealed record GeofenceResult(bool Inside, int WardId, string BoundaryVersion);
public sealed record WardCase(
    string Id, string Kind, string Title, string Status, string SlotCode,
    string Applicant, string Summary, GeoPoint? Location, string? EvidenceUrl,
    DateTime CreatedAt, string? Reason, string[] Blockers, string[] Actions,
    int? QueuePosition = null, string? ContractTerm = null, decimal? Outstanding = null);
public sealed record CasePage(IReadOnlyList<WardCase> Items, int Page, bool HasMore);
public sealed record ReviewDecision(string Decision, string Reason, string ExpectedStatus);

public interface IWardActorResolver
{
    Task<WardActor?> ResolveAsync(long userId, CancellationToken ct);
}

public interface IWardSlots
{
    Task<CasePage> ListAsync(WardActor actor, string kind, int page, CancellationToken ct);
    Task<WardCase> GetAsync(WardActor actor, string kind, long id, CancellationToken ct);
    Task<WardCase> DecideAsync(WardActor actor, string kind, long id, ReviewDecision decision, CancellationToken ct);
    Task<WardCase> PinAsync(WardActor actor, long id, GeoPoint point, CancellationToken ct);
}

public interface IGeolocation
{
    GeofenceResult Verify(int wardId, GeoPoint point);
}

public sealed class WardException(int status, string code, string message) : AppException(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public override int StatusCode => Status;
    public override string ErrorCode => Code;
}

public static class WardRules
{
    public static void Coordinates(GeoPoint point)
    {
        if (!double.IsFinite(point.Latitude) || !double.IsFinite(point.Longitude) ||
            point.Latitude is < -90 or > 90 || point.Longitude is < -180 or > 180)
            throw new WardException(400, "invalid_coordinates", "Tọa độ không hợp lệ.");
    }

    public static void Decision(ReviewDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.Reason) || decision.Reason.Trim().Length > 500)
            throw new WardException(400, "invalid_reason", "Nhập lý do quyết định từ 1 đến 500 ký tự.");
        if (string.IsNullOrWhiteSpace(decision.ExpectedStatus))
            throw new WardException(400, "missing_status", "Thiếu trạng thái hồ sơ đang xem.");
    }

    public static void Conflict(string message) => throw new WardException(409, "review_conflict", message);
    public static void NotFound() => throw new WardException(404, "not_found", "Không tìm thấy hồ sơ trong phường của bạn.");
}
