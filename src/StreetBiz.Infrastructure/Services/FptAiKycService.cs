using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.VendorKyc;

namespace StreetBiz.Infrastructure.Services;

/// <summary>
/// eKYC, FPT.AI primary + Gemini Vision fallback (same hybrid pattern AiComplianceService
/// already uses for ward-side CCCD checks -- see its remarks):
///   - ID Recognition  POST /vision/idr/vnm       header `api-key`,  form `image`
///   - Facematch       POST /dmp/checkface/v1     header `api_key`,  form `file[]` x2
/// The two FPT products use different key names and different header spellings; that is
/// FPT's API, not a typo here. FPT.AI's free-tier quota is small enough to exhaust during
/// ordinary testing (confirmed 2026-09-20: burst limit 4 req/s, 60 req/min) -- falling over to
/// Gemini (3-key pool, see AiKeyPools) keeps the wizard usable when that happens, instead of
/// forcing every applicant into manual entry.
///
/// Both sides of the card are read because the fields live on different faces: the front
/// carries id/name/dob/sex/nationality/address, while dân tộc, ngày cấp and nơi cấp exist
/// only on the back.
///
/// Everything returned is decision support (BR-41/42): the applicant may correct any value,
/// and a Ward Authority officer still confirms identity by hand before an enrollment can be
/// approved. A high face-match score means two photos show the same face -- it does not prove
/// the card is genuine or registered to that person, which would require FPT.IDCheck/C06.
/// </summary>
public sealed class FptAiKycService(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<FptAiKycService> logger,
    IFileStorage fileStorage,
    GeminiVisionClient geminiVision) : IKycVerificationService
{
    private const int LowConfidenceThreshold = 85;

    /// <summary>FPT.AI Facematch rejects anything that is not JPG (error code 408). Gemini has
    /// no such restriction, so this only gates the FPT.AI attempt, not the fallback.</summary>
    private static readonly string[] FaceMatchExtensions = [".jpg", ".jpeg"];

    public async Task<KycIdCardExtraction> ExtractIdCardAsync(
        string frontFileUrl, string? backFileUrl, CancellationToken ct)
    {
        var front = await TryReadFileAsync(frontFileUrl, ct);
        if (front is null)
        {
            return FailedExtraction(KycMessages.ProviderUnavailable);
        }

        var back = string.IsNullOrWhiteSpace(backFileUrl) ? null : await TryReadFileAsync(backFileUrl, ct);
        var hasBackPhoto = !string.IsNullOrWhiteSpace(backFileUrl);

        return await ExtractViaFptAsync(front.Value, hasBackPhoto ? back : null, hasBackPhoto, ct)
            ?? await ExtractViaGeminiAsync(front.Value, hasBackPhoto ? back : null, hasBackPhoto, ct)
            ?? FailedExtraction(KycMessages.ProviderUnavailable);
    }

    public async Task<KycFaceMatchResult> MatchFaceAsync(
        string selfieFileUrl, string idCardFrontFileUrl, CancellationToken ct)
    {
        var selfie = await TryReadFileAsync(selfieFileUrl, ct);
        var idCard = await TryReadFileAsync(idCardFrontFileUrl, ct);
        if (selfie is null || idCard is null)
        {
            return FailedFaceMatch(KycMessages.ProviderUnavailable);
        }

        return await MatchFaceViaFptAsync(selfie.Value, idCard.Value, ct)
            ?? await MatchFaceViaGeminiAsync(selfie.Value, idCard.Value, ct)
            ?? FailedFaceMatch(KycMessages.ProviderUnavailable);
    }

    #region Primary: FPT.AI
    /// <summary>Null means "could not get an answer from FPT.AI" (unconfigured, rate limited,
    /// unreachable) -- the caller falls back to Gemini. It does NOT mean "card unreadable";
    /// that case still returns a real (if empty) KycIdCardExtraction.</summary>
    private async Task<KycIdCardExtraction?> ExtractViaFptAsync(
        (byte[] Bytes, string MimeType) front, (byte[] Bytes, string MimeType)? back, bool hasBackPhoto, CancellationToken ct)
    {
        var apiKey = configuration["AiCompliance:FptAi:ApiKey"]?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogInformation("AiCompliance:FptAi:ApiKey is not configured -- trying Gemini for CCCD OCR.");
            return null;
        }

        var url = configuration["AiCompliance:FptAi:ApiUrl"]?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            url = "https://api.fpt.ai/vision/idr/vnm";
        }

        var frontData = await ReadSideAsync(url, apiKey, front, "mặt trước", ct);
        if (frontData is null)
        {
            return null;
        }

        JsonNode? backData = null;
        var warnings = new List<string>();
        if (hasBackPhoto && back is not null)
        {
            backData = await ReadSideAsync(url, apiKey, back.Value, "mặt sau", ct);
            if (backData is null)
            {
                warnings.Add("Không đọc được ảnh mặt sau CCCD — dân tộc, ngày cấp và nơi cấp cần nhập thủ công.");
            }
        }
        else
        {
            warnings.Add("Chưa có ảnh mặt sau CCCD — dân tộc, ngày cấp và nơi cấp cần nhập thủ công.");
        }

        // A card photographed the wrong way round is a common upload mistake: the front-side
        // call then returns back-side fields (and vice versa). Detect it rather than silently
        // pre-filling the form with nothing.
        if (Text(frontData, "id") is null && Text(frontData, "issue_date") is not null)
        {
            warnings.Add("Ảnh tải lên ở ô \"mặt trước\" có vẻ là mặt sau CCCD. Vui lòng kiểm tra lại.");
        }

        var idNumber = Text(frontData, "id");
        if (idNumber is not null && !(idNumber.Length == 12 && idNumber.All(char.IsDigit)))
        {
            warnings.Add($"Số CCCD đọc được (\"{idNumber}\") không đúng định dạng 12 chữ số — cán bộ cần đối chiếu bản gốc.");
        }

        var confidence = AverageConfidence(frontData, backData);
        var needsManual = confidence < LowConfidenceThreshold || warnings.Count > 0;

        return new KycIdCardExtraction(
            IdNumber: idNumber,
            FullName: Text(frontData, "name"),
            DateOfBirth: ParseDate(Text(frontData, "dob")),
            Gender: MapGender(Text(frontData, "sex")),
            Nationality: Text(frontData, "nationality"),
            Ethnicity: backData is null ? null : Text(backData, "ethnicity"),
            PermanentAddress: Text(frontData, "address") ?? Text(frontData, "home"),
            IdIssuedDate: backData is null ? null : ParseDate(Text(backData, "issue_date")),
            IdIssuedPlace: backData is null ? null : Text(backData, "issue_loc"),
            ConfidencePercent: confidence,
            NeedsManualVerification: needsManual,
            Warnings: warnings,
            IsAiGenerated: true,
            Summary: $"[AI - FPT.AI] Đã đọc {(backData is null ? "mặt trước" : "hai mặt")} CCCD, độ tin cậy {confidence}%. "
                   + "Vui lòng kiểm tra và sửa lại các trường chưa đúng trước khi nộp hồ sơ.");
    }

    /// <summary>Null means "could not get an answer from FPT.AI" -- caller falls back to Gemini.</summary>
    private async Task<KycFaceMatchResult?> MatchFaceViaFptAsync(
        (byte[] Bytes, string MimeType) selfie, (byte[] Bytes, string MimeType) idCard, CancellationToken ct)
    {
        var apiKey = configuration["AiCompliance:FptAi:FaceMatchApiKey"]?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogInformation("AiCompliance:FptAi:FaceMatchApiKey is not configured -- trying Gemini for face match.");
            return null;
        }

        // Checked before the call so an unsupported upload costs no provider credit. Gemini has
        // no such restriction, so an unsupported format still gets a Gemini attempt.
        if (!selfie.MimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
            || !idCard.MimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Face match photos are not JPG -- FPT.AI would reject them; trying Gemini instead.");
            return null;
        }

        var url = configuration["AiCompliance:FptAi:FaceMatchApiUrl"]?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            url = "https://api.fpt.ai/dmp/checkface/v1";
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("api_key", apiKey);

            using var content = new MultipartFormDataContent();
            content.Add(FilePart(selfie), "file[]", "selfie.jpg");
            content.Add(FilePart(idCard), "file[]", "idcard.jpg");
            request.Content = content;

            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            // FPT's rate-limit response ({"message":"API rate limit exceeded."}) has no "code"
            // field at all, so it falls into the same "could not get an answer" null path as a
            // network failure below -- which is exactly what should trigger the Gemini fallback,
            // as opposed to a genuine "407 no face detected" result (a real answer, kept as-is).
            var json = JsonNode.Parse(body);
            var code = json?["code"]?.GetValue<string>();

            if (code is null)
            {
                logger.LogWarning(
                    "FPT.AI Facematch gave no usable answer ({StatusCode}): {Body} -- trying Gemini instead.",
                    response.StatusCode, body);
                return null;
            }

            if (!response.IsSuccessStatusCode || code != "200")
            {
                logger.LogInformation("FPT.AI Facematch returned code {Code}: {Body}", code, body);
                return FailedFaceMatch(FaceMatchErrorMessage(code));
            }

            var data = json?["data"];
            var isMatch = data?["isMatch"]?.GetValue<bool>() ?? false;
            var similarity = data?["similarity"]?.GetValue<double>() ?? 0;
            var bothIdCards = data?["isBothImgIDCard"]?.GetValue<bool>() ?? false;

            var warnings = new List<string>();
            if (bothIdCards)
            {
                warnings.Add("Cả hai ảnh đều là giấy tờ tuỳ thân — ảnh chân dung phải là ảnh chụp trực tiếp khuôn mặt.");
            }
            if (!isMatch)
            {
                warnings.Add("Khuôn mặt trong ảnh chân dung không khớp với ảnh trên CCCD.");
            }

            return new KycFaceMatchResult(
                IsMatch: isMatch && !bothIdCards,
                SimilarityPercent: Math.Round(similarity, 2),
                BothImagesAreIdCards: bothIdCards,
                // Never "verified" outright: this only proves the two photos show the same face,
                // not that the card itself is genuine (BR-41 -- an officer still confirms).
                NeedsManualVerification: !isMatch || bothIdCards,
                Warnings: warnings,
                IsAiGenerated: true,
                Summary: isMatch && !bothIdCards
                    ? $"[AI - FPT.AI] Ảnh chân dung khớp với ảnh trên CCCD (độ tương đồng {similarity:0.##}%)."
                    : $"[AI - FPT.AI] Chưa khớp: độ tương đồng {similarity:0.##}% (ngưỡng đạt của FPT.AI là 80%).");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exception calling FPT.AI Facematch -- trying Gemini instead.");
            return null;
        }
    }
    #endregion

    #region Fallback: Gemini Vision
    /// <summary>
    /// Reads the same fields FPT.AI would, from one Gemini call with both card photos attached
    /// (Gemini takes multiple images per request, so front+back go in together rather than as
    /// two round trips). Unlike FPT.AI's per-field confidence scores, this is a single holistic
    /// confidence from the model, labelled distinctly ("Gemini Vision") so the reviewing officer
    /// never confuses it with FPT.AI's calibrated per-field output.
    /// </summary>
    private async Task<KycIdCardExtraction?> ExtractViaGeminiAsync(
        (byte[] Bytes, string MimeType) front, (byte[] Bytes, string MimeType)? back, bool hasBackPhoto, CancellationToken ct)
    {
        const string prompt = """
            Bạn là chuyên gia thị giác máy tính đọc Căn cước công dân (CCCD) Việt Nam để hỗ trợ điền
            trước biểu mẫu đăng ký hộ kinh doanh. Ảnh thứ nhất là MẶT TRƯỚC CCCD. Nếu có ảnh thứ hai,
            đó là MẶT SAU CCCD.

            Chỉ ghi nhận thông tin đọc được trực tiếp, tuyệt đối không suy đoán hay bịa thêm.

            Từ MẶT TRƯỚC, đọc: idNumber (đúng 12 chữ số liên tiếp, null nếu không đủ/không rõ), fullName
            (in hoa), dob (DD/MM/YYYY), gender ("Nam" hoặc "Nữ"), nationality, permanentAddress (địa chỉ
            thường trú đầy đủ).

            Từ MẶT SAU (chỉ khi có ảnh thứ hai), đọc: ethnicity (dân tộc), idIssuedDate (ngày cấp,
            DD/MM/YYYY), idIssuedPlace (nơi cấp, ví dụ "Cục Cảnh sát QLHC về TTXH").

            confidencePercent (0-100): 100 nếu ảnh rõ nét đọc chắc chắn; trừ điểm nếu mờ, nghiêng, loá
            sáng, hoặc có dấu hiệu chụp lại màn hình/photocopy/chỉnh sửa; dưới 50 nếu nghi giả mạo.
            warning: mô tả ngắn gọn nếu nghi giả mạo hoặc ảnh kém chất lượng, null nếu ảnh tốt.

            CHỈ trả về DUY NHẤT JSON hợp lệ, không markdown, không giải thích:
            {
              "idNumber": "chuỗi 12 chữ số hoặc null",
              "fullName": "hoặc null",
              "dob": "DD/MM/YYYY hoặc null",
              "gender": "Nam/Nữ hoặc null",
              "nationality": "hoặc null",
              "permanentAddress": "hoặc null",
              "ethnicity": "hoặc null (chỉ có nếu có ảnh mặt sau)",
              "idIssuedDate": "DD/MM/YYYY hoặc null (chỉ có nếu có ảnh mặt sau)",
              "idIssuedPlace": "hoặc null (chỉ có nếu có ảnh mặt sau)",
              "confidencePercent": 0-100,
              "warning": "mô tả ngắn hoặc null"
            }
            """;

        var images = hasBackPhoto && back is not null
            ? new[] { front, back.Value }
            : [front];

        var (success, json) = await geminiVision.CallAsync(prompt, images, ct);
        if (!success || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var doc = JsonNode.Parse(json);
            if (doc is null)
            {
                return null;
            }

            var warnings = new List<string>();
            var warning = doc["warning"]?.GetValue<string?>();
            if (!string.IsNullOrWhiteSpace(warning))
            {
                warnings.Add(warning);
            }
            if (!hasBackPhoto)
            {
                warnings.Add("Chưa có ảnh mặt sau CCCD — dân tộc, ngày cấp và nơi cấp cần nhập thủ công.");
            }

            var confidence = doc["confidencePercent"]?.GetValue<int?>() ?? 0;

            return new KycIdCardExtraction(
                IdNumber: doc["idNumber"]?.GetValue<string?>(),
                FullName: doc["fullName"]?.GetValue<string?>(),
                DateOfBirth: ParseDate(doc["dob"]?.GetValue<string?>()),
                Gender: MapGender(doc["gender"]?.GetValue<string?>()),
                Nationality: doc["nationality"]?.GetValue<string?>(),
                Ethnicity: hasBackPhoto ? doc["ethnicity"]?.GetValue<string?>() : null,
                PermanentAddress: doc["permanentAddress"]?.GetValue<string?>(),
                IdIssuedDate: hasBackPhoto ? ParseDate(doc["idIssuedDate"]?.GetValue<string?>()) : null,
                IdIssuedPlace: hasBackPhoto ? doc["idIssuedPlace"]?.GetValue<string?>() : null,
                ConfidencePercent: confidence,
                NeedsManualVerification: confidence < LowConfidenceThreshold || warnings.Count > 0,
                Warnings: warnings,
                IsAiGenerated: true,
                Summary: $"[AI - Gemini Vision] FPT.AI tạm thời không phản hồi (hết lượt dùng miễn phí/quá tải), "
                       + $"đã dùng Gemini để đọc {(hasBackPhoto ? "hai mặt" : "mặt trước")} CCCD, độ tin cậy {confidence}%. "
                       + "Vui lòng kiểm tra và sửa lại các trường chưa đúng trước khi nộp hồ sơ.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse Gemini Vision response for CCCD OCR fallback.");
            return null;
        }
    }

    /// <summary>
    /// Gemini has no dedicated face-recognition model and returns no calibrated similarity
    /// score the way FPT.AI's Facematch does -- this is a vision-language model's visual
    /// judgment, reported honestly as such rather than dressed up with a fabricated precise
    /// percentage. Framed as a coarse 3-level call (rõ ràng giống / có thể giống / không giống)
    /// specifically so a "60%"-looking number is never presented as if it meant the same thing
    /// as FPT.AI's threshold-calibrated metric.
    /// </summary>
    private async Task<KycFaceMatchResult?> MatchFaceViaGeminiAsync(
        (byte[] Bytes, string MimeType) selfie, (byte[] Bytes, string MimeType) idCard, CancellationToken ct)
    {
        const string prompt = """
            Bạn là trợ lý thị giác so sánh khuôn mặt để hỗ trợ (không thay thế) cán bộ xác minh danh
            tính. Ảnh thứ nhất là ẢNH CHÂN DUNG do người dùng tự chụp. Ảnh thứ hai là ẢNH TRÊN CCCD.

            So sánh đặc điểm khuôn mặt (hình dạng mặt, mắt, mũi, miệng, tỷ lệ) giữa hai ảnh và đánh giá
            khả năng đây là cùng một người. Đây là ước lượng thị giác của mô hình ngôn ngữ, KHÔNG phải
            thuật toán sinh trắc học chuyên dụng -- tuyệt đối không tự bịa ra một con số phần trăm có vẻ
            chính xác; chỉ chọn MỘT trong ba mức sau cho "verdict":
              "LIKELY_SAME"    - đặc điểm khuôn mặt khớp rõ ràng
              "UNCERTAIN"      - ảnh mờ/góc chụp xấu/khó xác định, hoặc có vài điểm khác biệt
              "LIKELY_DIFFERENT" - đặc điểm khuôn mặt khác biệt rõ ràng

            noFaceDetected: true nếu một trong hai ảnh không nhìn thấy rõ khuôn mặt người.
            reasoning: 1 câu ngắn giải thích căn cứ.

            CHỈ trả về DUY NHẤT JSON hợp lệ, không markdown:
            {
              "verdict": "LIKELY_SAME" | "UNCERTAIN" | "LIKELY_DIFFERENT",
              "noFaceDetected": true hoặc false,
              "reasoning": "1 câu"
            }
            """;

        var (success, json) = await geminiVision.CallAsync(prompt, [selfie, idCard], ct);
        if (!success || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var doc = JsonNode.Parse(json);
            if (doc is null)
            {
                return null;
            }

            var verdict = doc["verdict"]?.GetValue<string?>() ?? "UNCERTAIN";
            var noFace = doc["noFaceDetected"]?.GetValue<bool?>() ?? false;
            var reasoning = doc["reasoning"]?.GetValue<string?>() ?? "";

            if (noFace)
            {
                return new KycFaceMatchResult(
                    IsMatch: false,
                    SimilarityPercent: 0,
                    BothImagesAreIdCards: false,
                    NeedsManualVerification: true,
                    Warnings: ["Không nhận diện được khuôn mặt rõ ràng trong một trong hai ảnh."],
                    IsAiGenerated: true,
                    Summary: "[AI - Gemini Vision] FPT.AI tạm thời không phản hồi. Không nhận diện được khuôn mặt rõ ràng — vui lòng chụp lại ảnh chân dung.");
            }

            var isMatch = verdict == "LIKELY_SAME";
            var warnings = verdict == "LIKELY_DIFFERENT"
                ? new List<string> { "Đặc điểm khuôn mặt có vẻ khác biệt giữa ảnh chân dung và ảnh CCCD." }
                : new List<string>();

            return new KycFaceMatchResult(
                IsMatch: isMatch,
                // No fabricated precision: an indicative midpoint for each bucket, not a
                // calibrated measurement -- the verdict/summary text is the real signal.
                SimilarityPercent: verdict switch { "LIKELY_SAME" => 85, "LIKELY_DIFFERENT" => 30, _ => 55 },
                BothImagesAreIdCards: false,
                NeedsManualVerification: verdict != "LIKELY_SAME",
                Warnings: warnings,
                IsAiGenerated: true,
                Summary: $"[AI - Gemini Vision] FPT.AI tạm thời không phản hồi (hết lượt dùng miễn phí/quá tải), "
                       + $"đã dùng Gemini ước lượng thị giác thay thế: {VerdictLabel(verdict)}. {reasoning} "
                       + "Đây không phải thuật toán sinh trắc học chuyên dụng như FPT.AI — cán bộ cần đối chiếu kỹ hơn.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse Gemini Vision response for face match fallback.");
            return null;
        }
    }

    private static string VerdictLabel(string verdict) => verdict switch
    {
        "LIKELY_SAME" => "có vẻ là cùng một người",
        "LIKELY_DIFFERENT" => "có vẻ khác người",
        _ => "chưa thể kết luận rõ ràng",
    };
    #endregion

    private async Task<JsonNode?> ReadSideAsync(
        string url, string apiKey, (byte[] Bytes, string MimeType) file, string sideLabel, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("api-key", apiKey);

            using var content = new MultipartFormDataContent();
            content.Add(FilePart(file), "image", $"cccd-{sideLabel}.jpg");
            request.Content = content;

            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "FPT.AI ID Recognition ({Side}) failed with {StatusCode}: {Body}",
                    sideLabel, response.StatusCode, body);
                return null;
            }

            var json = JsonNode.Parse(body);
            if ((json?["errorCode"]?.GetValue<int>() ?? -1) != 0)
            {
                logger.LogWarning(
                    "FPT.AI ID Recognition ({Side}) returned errorCode {Code}: {Message}",
                    sideLabel, json?["errorCode"], json?["errorMessage"]);
                return null;
            }

            return json?["data"]?.AsArray().FirstOrDefault();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exception calling FPT.AI ID Recognition ({Side}).", sideLabel);
            return null;
        }
    }

    private async Task<(byte[] Bytes, string MimeType)?> TryReadFileAsync(string fileUrl, CancellationToken ct)
    {
        if (!EvidenceFiles.TryParseUrl(fileUrl, out var ownerUserId, out var fileName))
        {
            return null;
        }

        try
        {
            await using var stream = await fileStorage.OpenReadAsync(
                EvidenceFiles.StoragePath(ownerUserId, fileName), ct);
            if (stream is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var mime = EvidenceFiles.ContentTypes.TryGetValue(Path.GetExtension(fileName), out var type)
                ? type
                : "image/jpeg";
            return (buffer.ToArray(), mime);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read uploaded eKYC file {Url}.", fileUrl);
            return null;
        }
    }

    private static ByteArrayContent FilePart((byte[] Bytes, string MimeType) file)
    {
        var part = new ByteArrayContent(file.Bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(file.MimeType);
        return part;
    }

    private static bool IsJpeg(string fileUrl) =>
        FaceMatchExtensions.Contains(Path.GetExtension(fileUrl), StringComparer.OrdinalIgnoreCase);

    /// <summary>FPT.AI Facematch error codes, per its API documentation.</summary>
    private static string FaceMatchErrorMessage(string? code) => code switch
    {
        "407" => "[Hệ thống — chưa xác minh bằng AI] Không tìm thấy khuôn mặt trong ảnh. Chụp lại rõ mặt, đủ sáng, khuôn mặt chiếm ít nhất 1/4 khung hình.",
        "408" => KycMessages.JpegOnlyForFaceMatch,
        "409" => "[Hệ thống — chưa xác minh bằng AI] Cần đúng 2 ảnh: ảnh chân dung và ảnh mặt trước CCCD.",
        _ => KycMessages.ProviderUnavailable,
    };

    private static KycIdCardExtraction FailedExtraction(string summary) => new(
        null, null, null, null, null, null, null, null, null,
        ConfidencePercent: 0,
        NeedsManualVerification: true,
        Warnings: [summary],
        IsAiGenerated: false,
        Summary: summary);

    private static KycFaceMatchResult FailedFaceMatch(string summary) => new(
        IsMatch: false,
        SimilarityPercent: 0,
        BothImagesAreIdCards: false,
        NeedsManualVerification: true,
        Warnings: [summary],
        IsAiGenerated: false,
        Summary: summary);

    private static string? Text(JsonNode? node, string key)
    {
        var value = node?[key]?.GetValue<string>()?.Trim();
        // FPT returns the literal "N/A" for fields a given card type does not carry.
        return string.IsNullOrWhiteSpace(value) || value.Equals("N/A", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
    }

    /// <summary>Averages every `*_prob` FPT returned for the sides that were read.</summary>
    private static int AverageConfidence(JsonNode? front, JsonNode? back)
    {
        var probabilities = new List<double>();
        Collect(front);
        Collect(back);
        return probabilities.Count == 0 ? 0 : (int)Math.Round(probabilities.Average() * 100);

        void Collect(JsonNode? node)
        {
            if (node is not JsonObject obj)
            {
                return;
            }

            foreach (var (key, value) in obj)
            {
                if (!key.EndsWith("_prob", StringComparison.Ordinal))
                {
                    continue;
                }

                // FPT sends these as strings ("0.98"), and as "N/A" for absent fields.
                if (double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                {
                    probabilities.Add(p);
                }
            }
        }
    }

    private static readonly string[] DateFormats = ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy"];

    private static DateOnly? ParseDate(string? value) =>
        value is not null
        && DateOnly.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static string? MapGender(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "NAM" or "MALE" => OwnerGenders.Male,
        "NỮ" or "NU" or "FEMALE" => OwnerGenders.Female,
        null or "" => null,
        _ => OwnerGenders.Other,
    };
}
