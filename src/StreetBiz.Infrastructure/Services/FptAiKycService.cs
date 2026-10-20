using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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
/// ordinary testing (confirmed 2026-09-20: burst limit 4 req/s, 60 req/min). Each product takes a
/// pool of keys (AiCompliance:FptAi:ApiKeys / FaceMatchApiKeys, or the single ApiKey /
/// FaceMatchApiKey), rotated like Gemini's: a key that is rate limited, out of quota, refused or
/// unreachable hands over to the next FPT.AI key, and only when every FPT.AI key fails does the
/// request fall over to Gemini (its own key pool, see AiKeyPools). An image FPT.AI could read but
/// rejected (no card found, no face) is an answer about the image, so it is not retried on
/// another key.
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
    GeminiVisionClient geminiVision,
    AiKeyPools keyPools) : IKycVerificationService
{
    private const int LowConfidenceThreshold = 85;

    /// <summary>FPT.AI Facematch rejects anything that is not JPG (error code 408). Gemini has
    /// no such restriction, so this only gates the FPT.AI attempt, not the fallback.</summary>
    private static readonly string[] FaceMatchExtensions = [".jpg", ".jpeg"];

    /// <summary>Longest a vendor waits for one eKYC request (every FPT.AI key, then Gemini), under the
    /// web client's 60 s timeout for these two endpoints. Past it the answer is "enter it by hand".</summary>
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(45);

    public async Task<KycIdCardExtraction> ExtractIdCardAsync(
        string frontFileUrl, string? backFileUrl, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RequestBudget);
        try
        {
            return await ExtractIdCardWithinBudgetAsync(frontFileUrl, backFileUrl, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("CCCD OCR ran past its {Seconds} s budget.", RequestBudget.TotalSeconds);
            return FailedExtraction(KycMessages.TookTooLong);
        }
    }

    public async Task<KycFaceMatchResult> MatchFaceAsync(
        string selfieFileUrl, string idCardFrontFileUrl, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RequestBudget);
        try
        {
            return await MatchFaceWithinBudgetAsync(selfieFileUrl, idCardFrontFileUrl, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Face match ran past its {Seconds} s budget.", RequestBudget.TotalSeconds);
            return FailedFaceMatch(KycMessages.TookTooLong);
        }
    }

    private async Task<KycIdCardExtraction> ExtractIdCardWithinBudgetAsync(
        string frontFileUrl, string? backFileUrl, CancellationToken ct)
    {
        var front = await TryReadFileAsync(frontFileUrl, ct);
        if (front is null)
        {
            return FailedExtraction(KycMessages.ProviderUnavailable);
        }

        var back = string.IsNullOrWhiteSpace(backFileUrl) ? null : await TryReadFileAsync(backFileUrl, ct);
        var hasBackPhoto = !string.IsNullOrWhiteSpace(backFileUrl);

        var fpt = await ExtractViaFptAsync(front.Value, hasBackPhoto ? back : null, hasBackPhoto, ct);
        if (fpt is { BackKeysExhausted: true } && back is not null)
        {
            // FPT.AI read the front, then ran out of quota on the back: Gemini reads the back
            // rather than leaving dân tộc / ngày cấp / nơi cấp to the vendor.
            var gemini = await ExtractViaGeminiAsync(front.Value, back, hasBackPhoto: true, ct);
            return gemini is null ? fpt.Result : MergeBackFromGemini(fpt.Result, gemini);
        }

        return fpt?.Result
            ?? await ExtractViaGeminiAsync(front.Value, hasBackPhoto ? back : null, hasBackPhoto, ct)
            ?? FailedExtraction(KycMessages.ProviderUnavailable);
    }

    private async Task<KycFaceMatchResult> MatchFaceWithinBudgetAsync(
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
    private sealed record FptExtraction(KycIdCardExtraction Result, bool BackKeysExhausted);

    /// <summary>FPT.AI's front-side fields with Gemini's back-side ones (dân tộc, ngày cấp, nơi cấp).</summary>
    private static KycIdCardExtraction MergeBackFromGemini(KycIdCardExtraction fpt, KycIdCardExtraction gemini)
    {
        const string backMissing = "Không đọc được ảnh mặt sau CCCD — dân tộc, ngày cấp và nơi cấp cần nhập thủ công.";
        var warnings = fpt.Warnings.Where(w => w != backMissing)
            .Concat(gemini.Warnings.Where(w => !fpt.Warnings.Contains(w)))
            .ToList();
        var confidence = Math.Min(fpt.ConfidencePercent, gemini.ConfidencePercent);
        return fpt with
        {
            Ethnicity = gemini.Ethnicity,
            IdIssuedDate = gemini.IdIssuedDate,
            IdIssuedPlace = gemini.IdIssuedPlace,
            ConfidencePercent = confidence,
            NeedsManualVerification = confidence < LowConfidenceThreshold || warnings.Count > 0,
            Warnings = warnings,
            Summary = $"[AI - FPT.AI + Gemini Vision] FPT.AI đọc mặt trước; FPT.AI đã hết lượt nên Gemini đọc mặt sau. "
                    + $"Độ tin cậy {confidence}%. Vui lòng kiểm tra và sửa lại các trường chưa đúng trước khi nộp hồ sơ.",
        };
    }

    private async Task<FptExtraction?> ExtractViaFptAsync(
        (byte[] Bytes, string MimeType) front, (byte[] Bytes, string MimeType)? back, bool hasBackPhoto, CancellationToken ct)
    {
        var keys = keyPools.FptIdRecognition.GetAllKeysInOrder();
        if (keys.Length == 0)
        {
            logger.LogInformation("AiCompliance:FptAi:ApiKeys / ApiKey is not configured -- trying Gemini for CCCD OCR.");
            return null;
        }

        var url = configuration["AiCompliance:FptAi:ApiUrl"]?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            url = "https://api.fpt.ai/vision/idr/vnm";
        }

        var (frontData, keyIndex) = await ReadSideWithKeysAsync(url, keys, 0, front, "mặt trước", ct);
        if (frontData is null)
        {
            return null;
        }

        JsonNode? backData = null;
        var backKeysExhausted = false;
        var warnings = new List<string>();
        if (hasBackPhoto && back is not null)
        {
            // Start the back side on the key that just read the front: the keys before it have
            // already failed in this request.
            (backData, var backKey) = await ReadSideWithKeysAsync(url, keys, keyIndex, back.Value, "mặt sau", ct);
            backKeysExhausted = backKey < 0;
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

        return new FptExtraction(new KycIdCardExtraction(
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
                   + "Vui lòng kiểm tra và sửa lại các trường chưa đúng trước khi nộp hồ sơ."),
            backKeysExhausted);
    }

    /// <summary>Null means "could not get an answer from FPT.AI" -- caller falls back to Gemini.</summary>
    private async Task<KycFaceMatchResult?> MatchFaceViaFptAsync(
        (byte[] Bytes, string MimeType) selfie, (byte[] Bytes, string MimeType) idCard, CancellationToken ct)
    {
        var keys = keyPools.FptFaceMatch.GetAllKeysInOrder();
        if (keys.Length == 0)
        {
            logger.LogInformation("AiCompliance:FptAi:FaceMatchApiKeys / FaceMatchApiKey is not configured -- trying Gemini for face match.");
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

        for (var i = 0; i < keys.Length; i++)
        {
            var (keyUnusable, result) = await MatchFaceWithKeyAsync(url, keys[i], selfie, idCard, ct);
            if (!keyUnusable)
            {
                return result;
            }
            LogKeyHandOver("Facematch", "chân dung", keys[i], i, keys.Length);
        }

        logger.LogWarning("Every FPT.AI Facematch key failed -- trying Gemini instead.");
        return null;
    }

    /// <summary>One Facematch call with one key. KeyUnusable = the key, not the photos, is the
    /// problem (rate limited, out of quota, refused, FPT.AI down or unreachable): try the next key.
    /// Otherwise Result is FPT.AI's answer, including "no face detected".</summary>
    private async Task<(bool KeyUnusable, KycFaceMatchResult? Result)> MatchFaceWithKeyAsync(
        string url, string apiKey, (byte[] Bytes, string MimeType) selfie, (byte[] Bytes, string MimeType) idCard,
        CancellationToken ct)
    {
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

            if (code is null || IsKeyProblem(response.StatusCode))
            {
                logger.LogWarning(
                    "FPT.AI Facematch gave no usable answer ({StatusCode}): {Body}",
                    response.StatusCode, body);
                return (true, null);
            }

            if (!response.IsSuccessStatusCode || code != "200")
            {
                logger.LogInformation("FPT.AI Facematch returned code {Code}: {Body}", code, body);
                return (false, FailedFaceMatch(FaceMatchErrorMessage(code)));
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

            return (false, new KycFaceMatchResult(
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
                    : $"[AI - FPT.AI] Chưa khớp: độ tương đồng {similarity:0.##}% (ngưỡng đạt của FPT.AI là 80%)."));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Exception calling FPT.AI Facematch.");
            return (true, null);
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
            Bạn đọc Căn cước công dân (CCCD) / Thẻ căn cước Việt Nam để điền trước biểu mẫu đăng ký hộ
            kinh doanh. Ảnh thứ nhất là MẶT TRƯỚC. Nếu có ảnh thứ hai, đó là MẶT SAU. Nếu hai ảnh bị
            đảo (ảnh thứ nhất có mã MRZ/vân tay), vẫn đọc đúng từng mặt và ghi vào "warning".

            Nguyên tắc: chỉ chép đúng chữ in trên thẻ, giữ nguyên dấu tiếng Việt; trường không có
            hoặc không đọc rõ thì để null; tuyệt đối không suy đoán, không điền giá trị phổ biến.

            Có ba mẫu thẻ:
            - CCCD mã vạch (2016) và CCCD gắn chip (2021): mặt trước có Số, Họ và tên, Ngày sinh, Giới
              tính, Quốc tịch, Quê quán, Nơi thường trú. Mặt sau có ngày cấp ("Ngày, tháng, năm") và
              chức danh "Cục trưởng Cục Cảnh sát quản lý hành chính về trật tự xã hội".
            - Thẻ căn cước (2024): mặt trước có Số định danh cá nhân, Họ, chữ đệm và tên khai sinh,
              Ngày sinh, Giới tính, Quốc tịch. Mặt sau có Nơi cư trú, Nơi đăng ký khai sinh, Ngày cấp,
              cơ quan cấp là Bộ Công an.

            Các trường:
            - idNumber: đúng 12 chữ số của Số / Số định danh cá nhân; null nếu không đọc đủ 12 số.
            - fullName: họ tên viết HOA, đủ dấu, đúng như in.
            - dob: ngày sinh DD/MM/YYYY.
            - gender: "Nam" hoặc "Nữ".
            - nationality: quốc tịch như in (thường "Việt Nam").
            - permanentAddress: "Nơi thường trú" (mẫu 2016/2021) hoặc "Nơi cư trú" (mẫu 2024), nối các
              dòng bằng dấu phẩy. KHÔNG lấy "Quê quán" hay "Nơi đăng ký khai sinh".
            - ethnicity: dân tộc CHỈ khi thẻ có in mục "Dân tộc"; CCCD gắn chip và thẻ căn cước 2024
              không in mục này nên trả null, không được đoán.
            - idIssuedDate: ngày cấp DD/MM/YYYY ở mặt sau.
            - idIssuedPlace: nơi cấp ở mặt sau. Mẫu 2016/2021: "Cục Cảnh sát QLHC về TTXH" (viết gọn
              chức danh in trên thẻ). Mẫu 2024: "Bộ Công an".

            Đối chiếu bằng mã MRZ ở mặt sau (ba dòng bắt đầu bằng "IDVNM"): dòng 1 có 12 số CCCD sau
            9 số đầu; dòng 2 bắt đầu bằng ngày sinh YYMMDD, tiếp theo là chữ số kiểm tra và giới tính
            (M = Nam, F = Nữ). Nếu MRZ khác số CCCD, ngày sinh hoặc giới tính đọc ở mặt trước, giữ giá
            trị in ở mặt trước và ghi rõ chỗ lệch vào "warning".

            confidencePercent (0-100): 100 nếu ảnh rõ và mọi trường đọc chắc chắn, MRZ khớp; trừ điểm
            nếu mờ, nghiêng, lóa, che khuất, MRZ lệch; dưới 50 nếu có dấu hiệu chụp lại màn hình,
            photocopy hoặc chỉnh sửa.
            warning: mô tả ngắn các vấn đề trên bằng tiếng Việt, null nếu không có.

            CHỈ trả về DUY NHẤT một JSON hợp lệ, không markdown, không giải thích:
            {
              "idNumber": "chuỗi 12 chữ số hoặc null",
              "fullName": "hoặc null",
              "dob": "DD/MM/YYYY hoặc null",
              "gender": "Nam/Nữ hoặc null",
              "nationality": "hoặc null",
              "permanentAddress": "hoặc null",
              "ethnicity": "hoặc null",
              "idIssuedDate": "DD/MM/YYYY hoặc null",
              "idIssuedPlace": "hoặc null",
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

            var confidence = Math.Clamp(doc["confidencePercent"]?.GetValue<int?>() ?? 0, 0, 100);
            var geminiId = doc["idNumber"]?.GetValue<string?>()?.Trim();
            if (geminiId is not null && !(geminiId.Length == 12 && geminiId.All(char.IsDigit)))
            {
                warnings.Add($"Số CCCD đọc được (\"{geminiId}\") không đúng định dạng 12 chữ số — cán bộ cần đối chiếu bản gốc.");
            }

            return new KycIdCardExtraction(
                IdNumber: geminiId,
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
                Summary: $"[AI - Gemini Vision] FPT.AI không dùng được (hết lượt hoặc quá tải), "
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

    /// <summary>Reads one side of the card, trying the keys from <paramref name="start"/> on. Returns
    /// the side's data (null when FPT.AI answered that it could not read the image, or every key
    /// failed) and the index of the key that answered (-1 when none did).</summary>
    private async Task<(JsonNode? Data, int KeyIndex)> ReadSideWithKeysAsync(
        string url, string[] keys, int start, (byte[] Bytes, string MimeType) file, string sideLabel, CancellationToken ct)
    {
        for (var i = Math.Max(0, start); i < keys.Length; i++)
        {
            var (keyUnusable, data) = await ReadSideAsync(url, keys[i], file, sideLabel, ct);
            if (!keyUnusable)
            {
                return (data, i);
            }
            LogKeyHandOver("ID Recognition", sideLabel, keys[i], i, keys.Length);
        }

        logger.LogWarning("Every FPT.AI ID Recognition key failed for {Side}.", sideLabel);
        return (null, -1);
    }

    /// <summary>One ID Recognition call with one key. KeyUnusable = the key, not the image, is the
    /// problem: try the next key. Otherwise Data is the side's fields, or null when FPT.AI read the
    /// image and rejected it (no card found, bad image) -- another key would answer the same.</summary>
    private async Task<(bool KeyUnusable, JsonNode? Data)> ReadSideAsync(
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

            var json = TryParse(body);
            var errorCode = json?["errorCode"];
            // The rate-limit answer ({"message":"API rate limit exceeded."}) carries no errorCode;
            // a real answer about the image always does. "Out of transaction" (errorCode 1 on a 200)
            // is FPT.AI's out-of-credit answer: also the key's problem, not the image's.
            if (IsKeyProblem(response.StatusCode) || errorCode is null || IsOutOfCredit(json))
            {
                logger.LogWarning(
                    "FPT.AI ID Recognition ({Side}) failed with {StatusCode}: {Body}",
                    sideLabel, response.StatusCode, body);
                return (true, null);
            }

            if (!response.IsSuccessStatusCode || errorCode.GetValue<int>() != 0)
            {
                logger.LogWarning(
                    "FPT.AI ID Recognition ({Side}) returned errorCode {Code}: {Message}",
                    sideLabel, errorCode, json?["errorMessage"]);
                return (false, null);
            }

            return (false, json?["data"]?.AsArray().FirstOrDefault());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Exception calling FPT.AI ID Recognition ({Side}).", sideLabel);
            return (true, null);
        }
    }

    /// <summary>The key or the service is the problem, not the image: rate limited or out of quota
    /// (429), key rejected or suspended (401, 403), FPT.AI failing (5xx).</summary>
    private static bool IsKeyProblem(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        || (int)status >= 500;

    private static bool IsOutOfCredit(JsonNode? json) =>
        json?["errorMessage"]?.GetValue<string>() is { } message
        && (message.Contains("transaction", StringComparison.OrdinalIgnoreCase)
            || message.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || message.Contains("credit", StringComparison.OrdinalIgnoreCase));

    private static JsonNode? TryParse(string body)
    {
        try
        {
            return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Only the last 4 characters of a key are ever logged.</summary>
    private void LogKeyHandOver(string product, string sideLabel, string apiKey, int index, int total)
    {
        var suffix = apiKey.Length > 4 ? apiKey[^4..] : "****";
        if (index + 1 < total)
        {
            logger.LogWarning(
                "FPT.AI {Product} key [..{KeySuffix}] unusable ({Side}) -- trying FPT.AI key {Next}/{Total}.",
                product, suffix, sideLabel, index + 2, total);
        }
        else
        {
            logger.LogWarning(
                "FPT.AI {Product} key [..{KeySuffix}] unusable ({Side}) -- no FPT.AI key left.",
                product, suffix, sideLabel);
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
                    // Some card types come back as a fraction ("0.991"), others already as a
                    // percentage ("99.10"); without this a 99.1 % read was shown as 9910 %.
                    probabilities.Add(Math.Clamp(p > 1 ? p / 100 : p, 0, 1));
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
