namespace StreetBiz.Application.Features.VendorKyc;

/// <summary>
/// Fields read off a Vietnamese citizen ID card by OCR, used to pre-fill the REG-01 form
/// (Mẫu số 01 Phụ lục II, Thông tư 68/2025/TT-BTC). Every value is a suggestion the applicant
/// must review and may correct -- OCR is decision support, never the source of truth (BR-41).
///
/// Ethnicity/IdIssuedDate/IdIssuedPlace are printed on the BACK of the card only, so they stay
/// null when no back photo was supplied.
/// </summary>
public sealed record KycIdCardExtraction(
    string? IdNumber,
    string? FullName,
    DateOnly? DateOfBirth,
    string? Gender,
    string? Nationality,
    string? Ethnicity,
    string? PermanentAddress,
    DateOnly? IdIssuedDate,
    string? IdIssuedPlace,
    int ConfidencePercent,
    bool NeedsManualVerification,
    IReadOnlyList<string> Warnings,
    bool IsAiGenerated,
    string Summary);

/// <summary>
/// Result of comparing the applicant's portrait against the photo printed on their CCCD.
/// A high similarity is evidence, not proof of identity: it says the two photos show the same
/// face, not that the card is genuine or registered to that person in the national population
/// database. Only FPT.IDCheck/C06 could establish the latter, and it is not integrated.
/// </summary>
public sealed record KycFaceMatchResult(
    bool IsMatch,
    double SimilarityPercent,
    /// <summary>True when the provider detected TWO id cards instead of a portrait + a card,
    /// i.e. the applicant tried to match a card against itself.</summary>
    bool BothImagesAreIdCards,
    bool NeedsManualVerification,
    IReadOnlyList<string> Warnings,
    bool IsAiGenerated,
    string Summary);

/// <summary>Server-recorded outcome of one AI check, shown to the reviewing officer.</summary>
public sealed record KycCheckRecord(
    string CheckType,
    string Provider,
    bool? IsMatch,
    decimal? SimilarityPercent,
    int? ConfidencePercent,
    string? Warnings,
    DateTime CreatedAt);

/// <summary>Calls the eKYC provider. Implementations never write application state.</summary>
public interface IKycVerificationService
{
    /// <param name="backFileUrl">Optional: without it, dân tộc / ngày cấp / nơi cấp stay null.</param>
    Task<KycIdCardExtraction> ExtractIdCardAsync(string frontFileUrl, string? backFileUrl, CancellationToken ct);

    Task<KycFaceMatchResult> MatchFaceAsync(string selfieFileUrl, string idCardFrontFileUrl, CancellationToken ct);
}

/// <summary>Persists and reads back the AI check outcomes (see db/StreetBiz_SQL_Server.sql).</summary>
public interface IKycResultRepository
{
    Task RecordIdCardCheckAsync(long userId, KycIdCardExtraction extraction, CancellationToken ct);

    Task RecordFaceMatchAsync(long userId, KycFaceMatchResult result, CancellationToken ct);

    /// <summary>Attaches this user's not-yet-linked checks to the registration they belong to.</summary>
    Task LinkPendingResultsAsync(long userId, long registrationId, CancellationToken ct);

    /// <summary>The latest check of each type for a registration, for the reviewing officer.</summary>
    Task<IReadOnlyList<KycCheckRecord>> ListForRegistrationAsync(long registrationId, CancellationToken ct);
}

public static class KycCheckTypes
{
    public const string IdCardOcr = "ID_CARD_OCR";
    public const string FaceMatch = "FACE_MATCH";
}

public static class KycMessages
{
    public const string ConsentRequired =
        "Vui lòng đồng ý cho phép xử lý dữ liệu sinh trắc học trước khi quét CCCD hoặc đối chiếu khuôn mặt.";
    public const string FrontPhotoRequired = "Vui lòng tải ảnh mặt trước CCCD.";
    public const string SelfiePhotoRequired = "Vui lòng tải ảnh chân dung.";
    public const string NotConfigured =
        "[Hệ thống — chưa xác minh bằng AI] Dịch vụ eKYC chưa được cấu hình. Vui lòng nhập thông tin thủ công.";
    public const string ProviderUnavailable =
        "[Hệ thống — chưa xác minh bằng AI] Không gọi được dịch vụ eKYC. Vui lòng nhập thông tin thủ công, cán bộ sẽ đối chiếu trực tiếp.";
    public const string JpegOnlyForFaceMatch =
        "Đối chiếu khuôn mặt chỉ nhận ảnh JPG. Vui lòng tải lại ảnh chân dung và ảnh CCCD ở định dạng JPG.";
}
