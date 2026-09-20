using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.WardCompliance;

namespace StreetBiz.Infrastructure.Services;

/// <summary>
/// AI Compliance Service combining:
/// 1. Google Gemini (Multimodal Vision): OCR for Vietnamese CCCD and sidewalk encroachment detection.
/// 2. Groq LLM (Ultra-fast Llama 3.3): Legal citation classification, penalty drafting, and vendor assistant.
/// </summary>
public sealed class AiComplianceService : IAiComplianceService
{
    private const int LowConfidenceThreshold = 85;

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AiComplianceService> _logger;
    private readonly IFileStorage? _fileStorage;
    private readonly ApiKeyPool _geminiKeyPool;
    private readonly ApiKeyPool _groqKeyPool;

    public AiComplianceService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<AiComplianceService> logger,
        AiKeyPools keyPools,
        IFileStorage? fileStorage = null)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _fileStorage = fileStorage;

        // keyPools is a Singleton (see AiKeyPools' remarks) so the round-robin counter
        // survives across requests -- this service itself is Transient (AddHttpClient).
        _geminiKeyPool = keyPools.Gemini;
        _groqKeyPool = keyPools.Groq;
    }

    #region Hybrid KYC: FPT.AI (Primary) + Gemini Vision (Fallback)
    private async Task<(bool Success, AiIdExtractionResult? Result)> CallFptAiIdRecognitionAsync(
        byte[] imageBytes, string mimeType, CancellationToken ct)
    {
        var fptApiKey = _configuration["AiCompliance:FptAi:ApiKey"]?.Trim();
        if (string.IsNullOrWhiteSpace(fptApiKey))
        {
            return (false, null);
        }

        var apiUrl = _configuration["AiCompliance:FptAi:ApiUrl"]?.Trim();
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            apiUrl = "https://api.fpt.ai/vision/idr/vnm";
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            request.Headers.Add("api-key", fptApiKey);

            using var content = new MultipartFormDataContent();
            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            content.Add(imageContent, "image", "id_card.jpg");
            request.Content = content;

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "FPT.AI ID Recognition HTTP call failed with code {StatusCode}: {Error}. Falling back to Gemini Vision...",
                    response.StatusCode,
                    responseBody);
                return (false, null);
            }

            var doc = JsonNode.Parse(responseBody);
            var errorCode = doc?["errorCode"]?.GetValue<int?>() ?? -1;
            if (errorCode != 0)
            {
                var errorMsg = doc?["errorMessage"]?.GetValue<string?>() ?? "Unknown FPT error";
                _logger.LogWarning(
                    "FPT.AI ID Recognition returned errorCode {ErrorCode}: {ErrorMessage}. Falling back to Gemini Vision...",
                    errorCode,
                    errorMsg);
                return (false, null);
            }

            var dataArr = doc?["data"]?.AsArray();
            if (dataArr == null || dataArr.Count == 0)
            {
                _logger.LogWarning("FPT.AI ID Recognition returned empty data. Falling back to Gemini Vision...");
                return (false, null);
            }

            var item = dataArr[0];
            var idNumber = item?["id"]?.GetValue<string?>();
            var fullName = item?["name"]?.GetValue<string?>();
            var address = item?["address"]?.GetValue<string?>();

            // Calculate confidence from prob fields if available
            var idProb = double.TryParse(item?["id_prob"]?.GetValue<string?>(), out var ip) ? ip : 95.0;
            var nameProb = double.TryParse(item?["name_prob"]?.GetValue<string?>(), out var np) ? np : 95.0;
            var addrProb = double.TryParse(item?["address_prob"]?.GetValue<string?>(), out var ap) ? ap : 90.0;
            var confidence = (int)Math.Round((idProb + nameProb + addrProb) / 3.0);

            _logger.LogInformation("FPT.AI ID Recognition succeeded for ID {IdNumber} (Confidence: {Confidence}%).", idNumber, confidence);

            return (true, new AiIdExtractionResult(
                idNumber,
                fullName,
                address,
                confidence,
                IsAiGenerated: true,
                Source: "FPT.AI",
                TamperingAlert: null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception calling FPT.AI ID Recognition API. Falling back to Gemini Vision...");
            return (false, null);
        }
    }

    public async Task<AiIdExtractionResult> ExtractIdDocumentAsync(
        IReadOnlyList<WardEvidenceDto> evidence,
        CancellationToken ct)
    {
        var idPhoto = evidence.FirstOrDefault(e =>
            e.Type.Contains("ID", StringComparison.OrdinalIgnoreCase) ||
            e.Type.Contains("CCCD", StringComparison.OrdinalIgnoreCase)) ?? evidence.FirstOrDefault();

        if (idPhoto is null)
        {
            return new AiIdExtractionResult(null, null, null, 0, IsAiGenerated: false);
        }

        var image = await TryFetchImageAsync(idPhoto.FileUrl, ct);
        if (image is null)
        {
            _logger.LogWarning("Could not fetch evidence image {Url} for OCR extraction.", idPhoto.FileUrl);
            return new AiIdExtractionResult(null, null, null, 0, IsAiGenerated: false);
        }

        // 1. Primary: Try FPT.AI ID Recognition
        var (fptSuccess, fptResult) = await CallFptAiIdRecognitionAsync(image.Value.Bytes, image.Value.MimeType, ct);
        if (fptSuccess && fptResult != null)
        {
            return fptResult;
        }

        // 2. Secondary: Fallback to Gemini Vision with Master Prompt
        _logger.LogInformation("Using Gemini Vision (3-key pool) for CCCD extraction and anti-fraud analysis...");

        const string prompt = """
            Bạn là Chuyên gia Giám định Kỹ thuật số & Thị giác Máy tính cấp cao của Hệ thống Quản lý Vỉa hè Đô thị StreetBiz (Việt Nam).
            Nhiệm vụ của bạn là thẩm định tính xác thực của ảnh Căn cước công dân (CCCD/CMND) và bóc tách dữ liệu phục vụ xét duyệt hồ sơ đăng ký kinh doanh vỉa hè.

            HÃY THỰC HIỆN ĐẦY ĐỦ 3 BƯỚC THẨM ĐỊNH SAU ĐÂY:

            BƯỚC 1: BÓC TÁCH DỮ LIỆU ĐỊNH DANH (OCR TRÍCH XUẤT NGUYÊN BẢN)
            Chỉ ghi nhận thông tin đọc được trực tiếp trên ảnh, tuyệt đối không suy đoán:
            - idNumber: Số CCCD. Quy chuẩn bắt buộc: Phải gồm đúng 12 chữ số liên tiếp. Nếu ảnh mờ, mất góc, không đủ 12 chữ số -> gán giá trị null.
            - fullName: Họ và tên đầy đủ in hoa trên thẻ (ví dụ: "NGUYỄN VĂN AN").
            - address: Nơi thường trú ghi trên thẻ (đầy đủ số nhà, đường, phường/xã, quận/huyện, tỉnh/thành phố).
            - dob: Ngày tháng năm sinh (định dạng DD/MM/YYYY nếu đọc được, ngược lại null).

            BƯỚC 2: KIỂM TRA TÍNH TOÀN VẸN & PHÒNG CHỐNG GIAN LẬN (ANTI-FRAUD & INTEGRITY CHECK)
            Quan sát kỹ toàn bộ bức ảnh và phát hiện các dấu hiệu vi phạm sau:
            1. Recaptured Screen (Chụp lại từ màn hình): Có sọc nhiễu moiré, ánh sáng phản chiếu bóng đèn trên kính màn hình, viền khung vi tính/điện thoại hay không?
            2. Photocopy / Bản in lại: Ảnh đen trắng, mất màu tem quốc huy/hoa văn phôi thẻ chuẩn của Bộ Công an hay không?
            3. Cắt ghép / Can thiệp đồ họa: Các dòng chữ có bị lệch font, mờ nhòe bất thường so với nền thẻ, hoặc có dấu hiệu photoshop/chắp vá ảnh hay không?
            4. Chất lượng ảnh: Ảnh có bị lóa đèn flash ngay cụm số định danh hoặc bị che khuất ngón tay hay không?

            BƯỚC 3: TÍNH TOÁN ĐIỂM TIN CẬY (confidencePercent: 0 - 100)
            - Mặc định ảnh rõ nét, hợp lệ: 100 điểm.
            - Trừ 20 điểm: Nếu ảnh hơi mờ hoặc góc chụp hơi nghiêng nhưng vẫn đọc được thông tin.
            - Trừ 40 điểm: Nếu phát hiện dấu hiệu chụp lại màn hình (recaptured screen) hoặc bản in photocopy.
            - Trừ 50 điểm: Nếu số CCCD bị mờ, lóa sáng hoặc không đủ 12 chữ số.
            - Nếu nghi ngờ giả mạo nghiêm trọng: confidencePercent < 50.

            QUY CÁCH ĐẦU RA (BẮT BUỘC):
            Chỉ trả về DUY NHẤT một chuỗi JSON hợp lệ, không dùng markdown ```json, không giải thích gì thêm:
            {
              "idNumber": "chuỗi 12 chữ số hoặc null",
              "fullName": "họ tên in hoa hoặc null",
              "address": "địa chỉ thường trú hoặc null",
              "dob": "DD/MM/YYYY hoặc null",
              "confidencePercent": 0-100,
              "tamperingAlert": "mô tả ngắn gọn nghi vấn nếu có (ví dụ: 'Nghi vấn chụp lại từ màn hình điện thoại', 'Ảnh bị lóa sáng che mất số định danh'), nếu ảnh tốt thì để null"
            }
            """;

        var (success, json) = await CallGeminiVisionAsync(prompt, image.Value.Bytes, image.Value.MimeType, ct);
        if (success && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var doc = JsonNode.Parse(json);
                if (doc != null)
                {
                    var idNumber = doc["idNumber"]?.GetValue<string?>();
                    var fullName = doc["fullName"]?.GetValue<string?>();
                    var address = doc["address"]?.GetValue<string?>();
                    var confidence = doc["confidencePercent"]?.GetValue<int>() ?? 0;
                    var tamperingAlert = doc["tamperingAlert"]?.GetValue<string?>();

                    return new AiIdExtractionResult(
                        idNumber,
                        fullName,
                        address,
                        confidence,
                        IsAiGenerated: true,
                        Source: "Gemini Vision",
                        TamperingAlert: tamperingAlert);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse Gemini Vision response for ExtractIdDocumentAsync.");
            }
        }

        return new AiIdExtractionResult(null, null, null, 0, IsAiGenerated: false);
    }

    public Task<AiDocumentCheckResult> CompareDeclaredProfileAsync(
        string declaredName,
        string? extractedIdNumber,
        string declaredAddress,
        AiIdExtractionResult extraction,
        CancellationToken ct)
    {
        if (!extraction.IsAiGenerated || string.IsNullOrWhiteSpace(extraction.IdNumber))
        {
            return Task.FromResult(new AiDocumentCheckResult(
                MatchPercentage: 0,
                IsMatch: false,
                NeedsManualVerification: true,
                Summary: "[Hệ thống — chưa xác minh bằng AI] Không trích xuất được dữ liệu từ ảnh CCCD. Cán bộ cần đối chiếu thủ công.",
                Discrepancies: new[] { "Không có kết quả OCR để đối chiếu." },
                IsAiGenerated: false));
        }

        var discrepancies = new List<string>();
        var points = 100;

        var nameMatches = !string.IsNullOrWhiteSpace(extraction.FullName)
            && NormalizeForCompare(extraction.FullName).Contains(NormalizeForCompare(declaredName));
        if (!nameMatches)
        {
            discrepancies.Add($"Họ tên trên CCCD ('{extraction.FullName}') không khớp tên khai báo ('{declaredName}').");
            points -= 40;
        }

        var idPlausible = extraction.IdNumber.Length == 12 && extraction.IdNumber.All(char.IsDigit);
        if (!idPlausible)
        {
            discrepancies.Add($"Số CCCD trích xuất ('{extraction.IdNumber}') không đúng định dạng 12 chữ số.");
            points -= 30;
        }

        if (!string.IsNullOrWhiteSpace(extraction.TamperingAlert))
        {
            discrepancies.Add($"Cảnh báo thị giác: {extraction.TamperingAlert}");
            points -= 25;
        }

        if (extraction.ConfidencePercent < LowConfidenceThreshold)
        {
            discrepancies.Add($"Độ tin cậy trích xuất thấp ({extraction.ConfidencePercent}%) — cần cán bộ xác minh lại bằng mắt thường.");
        }

        var matchScore = Math.Clamp(points, 0, 100);
        var needsManual = extraction.ConfidencePercent < LowConfidenceThreshold || matchScore < LowConfidenceThreshold || !string.IsNullOrWhiteSpace(extraction.TamperingAlert);

        var sourceTag = extraction.Source switch
        {
            "FPT.AI" => "[AI - FPT.AI]",
            "Gemini Vision" => "[AI - Gemini Vision]",
            _ => "[AI]"
        };

        var summary = discrepancies.Count == 0
            ? $"{sourceTag} Đã trích xuất và đối chiếu CCCD thành công (độ tin cậy {extraction.ConfidencePercent}%). Số CCCD: {extraction.IdNumber}, khớp {matchScore}% với thông tin khai báo."
            : $"{sourceTag} Phát hiện sai lệch khi đối chiếu CCCD (độ khớp {matchScore}%).";

        return Task.FromResult(new AiDocumentCheckResult(
            matchScore, matchScore >= LowConfidenceThreshold && string.IsNullOrWhiteSpace(extraction.TamperingAlert), needsManual, summary, discrepancies, IsAiGenerated: true));
    }
    #endregion

    #region Gemini Vision: Encroachment Analysis
    public async Task<AiEncroachmentResult> AnalyzeInspectionPhotoAsync(
        string photoUrl,
        double? slotWidth,
        double? slotLength,
        CancellationToken ct)
    {
        var image = await TryFetchImageAsync(photoUrl, ct);
        if (image is null)
        {
            _logger.LogWarning("Could not fetch inspection photo {Url} for AI Vision analysis.", photoUrl);
            return FallbackEncroachment();
        }

        var prompt = $@"
            Bạn là trợ lý thị giác đô thị kiểm tra trật tự vỉa hè tại Việt Nam.
            Căn cứ Luật Đường bộ 2024 và Nghị định 165/2024/NĐ-CP: hoạt động kinh doanh trên vỉa hè
            phải nằm trong phạm vi ô được cấp phép và chừa lối đi bộ thông thoáng tối thiểu 1.5m.
            Kích thước ô cấp phép: {slotWidth ?? 2}m (rộng) x {slotLength ?? 3}m (dài).

            Quan sát ảnh đính kèm và trả về ĐÚNG định dạng JSON sau (không kèm markdown):
            {{
              ""detectedEncroachment"": true hoặc false,
              ""encroachmentDistanceCm"": số ước tính (0 nếu không lấn),
              ""analysis"": ""mô tả ngắn gọn tiếng Việt những gì quan sát được trong ảnh"",
              ""visualCues"": [""dấu hiệu cụ thể quan sát được trong ảnh""]
            }}";

        var (success, json) = await CallGeminiVisionAsync(prompt, image.Value.Bytes, image.Value.MimeType, ct);
        if (success && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var doc = JsonNode.Parse(json);
                if (doc != null)
                {
                    var detected = doc["detectedEncroachment"]?.GetValue<bool>() ?? false;
                    var distance = doc["encroachmentDistanceCm"]?.GetValue<double>() ?? 0;
                    var analysis = doc["analysis"]?.GetValue<string>() ?? "Không có mô tả.";
                    var visualCues = doc["visualCues"]?.AsArray()
                        .Select(x => x?.ToString() ?? "")
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList() ?? new List<string>();

                    return new AiEncroachmentResult(detected, distance, $"[AI] {analysis}", visualCues, IsAiGenerated: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse Gemini Vision response for AnalyzeInspectionPhotoAsync.");
            }
        }

        return FallbackEncroachment();
    }
    #endregion

    #region Groq LLM: Legal Classification & Sanction Drafting (Mau MBB01 -- ND 118/2021/NĐ-CP)
    public async Task<AiLegalSuggestion> ClassifyAndDraftAsync(
        string? description,
        IReadOnlyList<PenaltyScheduleItemDto> availableSchedules,
        CancellationToken ct)
    {
        if (availableSchedules.Count == 0)
        {
            return new AiLegalSuggestion(
                "UNKNOWN", null, null, null,
                HanhViViPham: "Chưa xác định — Phường chưa cấu hình khung xử phạt (WARD-03).",
                BienPhapKhacPhuc: "",
                IsAiGenerated: false);
        }

        var text = (description ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            var def = availableSchedules[0];
            return DraftFromSchedule(def, hanhVi: $"Hành vi: {def.ViolationTypeName}.", isAiGenerated: false);
        }

        // Try Groq LLM for intelligent classification (which schedule fits the description),
        // never for the legal citation or amount -- those are always copied verbatim from the
        // matched PenaltyFeeSchedules row below, regardless of what the model returns.
        if (_groqKeyPool.HasKeys)
        {
            var schedulesJson = JsonSerializer.Serialize(availableSchedules.Select(s => new
            {
                s.ScheduleId,
                s.ViolationType,
                s.ViolationTypeName
            }));

            var systemPrompt = $$"""
                Bạn là trợ lý phân loại vi phạm trật tự vỉa hè tại Việt Nam.
                Nhiệm vụ DUY NHẤT: đọc mô tả hiện trường của cán bộ và chọn ĐÚNG MỘT 'scheduleId' phù hợp nhất
                từ danh sách khung xử phạt Phường đã cấu hình dưới đây, rồi tóm tắt lại hành vi bằng văn phong
                hành chính (không kèm căn cứ pháp lý, không kèm số tiền — hai mục đó hệ thống tự điền từ khung đã chọn).
                Trả về JSON duy nhất:
                {
                  "scheduleId": số ID khung phạt (bắt buộc phải có trong danh sách),
                  "hanhViViPham": "Mô tả hành vi vi phạm bằng văn phong biên bản hành chính, dựa trên mô tả hiện trường",
                  "bienPhapKhacPhuc": "Biện pháp khắc phục hậu quả đề xuất (VD: buộc thu dọn vật dụng lấn chiếm trong 2 giờ)"
                }

                Danh sách khung xử phạt của Phường (chỉ được chọn 1 trong các scheduleId sau, KHÔNG được tự tạo mã mới):
                {{schedulesJson}}
                """;

            var userPrompt = $"Mô tả vi phạm hiện trường: \"{text}\"";

            var (groqSuccess, groqResponse) = await CallGroqChatAsync(systemPrompt, userPrompt, ct, jsonMode: true);
            if (groqSuccess && !string.IsNullOrWhiteSpace(groqResponse))
            {
                try
                {
                    var doc = JsonNode.Parse(groqResponse);
                    var schedId = doc?["scheduleId"]?.GetValue<int?>();
                    var matched = availableSchedules.FirstOrDefault(s => s.ScheduleId == schedId);

                    if (matched != null)
                    {
                        var hanhVi = doc?["hanhViViPham"]?.GetValue<string?>();
                        var bienPhap = doc?["bienPhapKhacPhuc"]?.GetValue<string?>();
                        return DraftFromSchedule(
                            matched,
                            hanhVi: string.IsNullOrWhiteSpace(hanhVi) ? $"Hành vi: {matched.ViolationTypeName}." : hanhVi!,
                            bienPhap: bienPhap,
                            isAiGenerated: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse Groq response for ClassifyAndDraftAsync.");
                }
            }
        }

        // Deterministic keyword fallback (no LLM call succeeded or configured).
        var picked = PickSchedule(text.ToUpperInvariant(), availableSchedules);
        return DraftFromSchedule(picked, hanhVi: $"Hành vi: {picked.ViolationTypeName}.", isAiGenerated: false);
    }

    /// <summary>
    /// The only place AiLegalSuggestion is constructed with a real schedule -- LegalBasis and
    /// SuggestedPenaltyAmount always come from <paramref name="schedule"/> (the DB row), never
    /// from model output, no matter what the LLM path above parsed.
    /// </summary>
    private static AiLegalSuggestion DraftFromSchedule(
        PenaltyScheduleItemDto schedule, string hanhVi, bool isAiGenerated, string? bienPhap = null) =>
        new(
            schedule.ViolationType,
            schedule.ScheduleId,
            schedule.LegalBasis,
            schedule.PenaltyAmount,
            HanhViViPham: hanhVi,
            BienPhapKhacPhuc: string.IsNullOrWhiteSpace(bienPhap)
                ? "Buộc khôi phục lại tình trạng ban đầu, thu dọn vật dụng lấn chiếm trong thời hạn cán bộ ấn định."
                : bienPhap,
            IsAiGenerated: isAiGenerated);
    #endregion

    #region Groq LLM: Vendor Assistant Chatbot
    public async Task<string> AnswerVendorAssistantAsync(string question, string? context, CancellationToken ct)
    {
        if (!_groqKeyPool.HasKeys)
        {
            return "Trợ lý StreetBiz: Hệ thống đang vận hành ở chế độ cơ bản. Bạn có thể vào mục 'Ô thuê' để xem sơ đồ điểm bán, 'Đăng ký kinh doanh' để nộp hồ sơ, hoặc 'Tài chính' để tra cứu lệ phí tạm thời sử dụng hè phố.";
        }

        const string systemPrompt = """
            Bạn là Trợ lý StreetBiz - Cố vấn pháp lý và quy chuẩn vỉa hè cho Hộ kinh doanh tại Việt Nam (thí điểm Đà Nẵng).
            Căn cứ pháp lý áp dụng:
            - Luật Đường bộ 2024 (Điều 77: Sử dụng tạm thời một phần lòng đường, vỉa hè vào mục đích khác phải được cơ quan có thẩm quyền cấp phép).
            - Nghị định 165/2024/NĐ-CP (Điều 21: Thủ tục, điều kiện cấp phép sử dụng tạm thời lòng đường, hè phố).
            - Quy chuẩn hè phố: Phải chừa lại tối thiểu 1.5 mét cho người đi bộ, kinh doanh đúng vị trí ô được cấp phép và trong khung giờ quy định.
            - Nghị định 168/2024/NĐ-CP: Xử phạt vi phạm hành chính trong lĩnh vực giao thông đường bộ và trật tự đô thị.
            - Luật Phí và Lệ phí 2015: Thu phí sử dụng tạm thời vỉa hè theo ngày/tháng.

            Quy tắc ứng xử:
            1. Trả lời bằng tiếng Việt lịch sự, rõ ràng, dễ hiểu cho người bán hàng rong và hộ kinh doanh nhỏ.
            2. Trích dẫn đúng tên luật và nghị định khi liên quan đến quyền lợi hoặc xử phạt.
            3. Luôn lưu ý rằng các ý kiến tư vấn mang tính chất hướng dẫn, quyết định cấp phép và xử phạt thuộc thẩm quyền của UBND Phường.
            """;

        var userPrompt = string.IsNullOrWhiteSpace(context)
            ? question
            : $"Ngữ cảnh người dùng: {context}\n\nCâu hỏi: {question}";

        var (success, response) = await CallGroqChatAsync(systemPrompt, userPrompt, ct, jsonMode: false);
        if (success && !string.IsNullOrWhiteSpace(response))
        {
            return response;
        }

        return "Xin lỗi, hiện tại tôi chưa thể xử lý câu hỏi của bạn. Vui lòng liên hệ trực tiếp Bộ phận Tiếp nhận và Trả kết quả của UBND Phường để được hướng dẫn chi tiết.";
    }
    #endregion

    #region Helper Methods
    private static PenaltyScheduleItemDto PickSchedule(string descriptionUpper, IReadOnlyList<PenaltyScheduleItemDto> schedules)
    {
        (string Type, string[] Keywords)[] rules =
        [
            ("EXPIRED_OR_INVALID_PERMIT", ["HẾT HẠN", "KHÔNG PHÉP", "SAI NỘI DUNG", "EXPIRED", "INVALID"]),
            ("HYGIENE_LITTERING", ["RÁC", "VỆ SINH", "TRASH", "LITTER"]),
            ("OBSTRUCT_PUBLIC_ORDER", ["CẢN TRỞ", "AN NINH", "OBSTRUCT"]),
            ("STREET_VENDING_RESTRICTED", ["HÀNG RONG", "TUYẾN CẤM", "VENDING"]),
            ("UNAUTHORIZED_BUSINESS_USE", ["LẤN CHIẾM", "BÀY BÁN", "KINH DOANH", "ENCROACH"]),
        ];

        foreach (var (type, keywords) in rules)
        {
            if (keywords.Any(descriptionUpper.Contains) &&
                schedules.FirstOrDefault(s => s.ViolationType == type) is { } match)
            {
                return match;
            }
        }

        return schedules[0];
    }

    private static AiEncroachmentResult FallbackEncroachment() => new(
        DetectedEncroachment: false,
        EncroachmentDistanceCm: 0,
        Analysis: "[Hệ thống — chưa xác minh bằng AI] Không thể tải hoặc phân tích ảnh hiện trường. Cán bộ cần kiểm tra trực tiếp.",
        VisualCues: Array.Empty<string>(),
        IsAiGenerated: false);

    private static string NormalizeForCompare(string s) =>
        s.Trim().ToUpperInvariant();

    private async Task<(byte[] Bytes, string MimeType)?> TryFetchImageAsync(string url, CancellationToken ct)
    {
        // 1. If stored locally in EvidenceFiles storage, read directly from disk
        if (_fileStorage != null && EvidenceFiles.TryParseUrl(url, out var ownerUserId, out var fileName))
        {
            try
            {
                var storagePath = EvidenceFiles.StoragePath(ownerUserId, fileName);
                using var stream = await _fileStorage.OpenReadAsync(storagePath, ct);
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, ct);
                    var bytes = ms.ToArray();
                    var ext = Path.GetExtension(fileName);
                    var mime = EvidenceFiles.ContentTypes.TryGetValue(ext, out var ctType) ? ctType : "image/jpeg";
                    return (bytes, mime);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read evidence file from local storage: {Url}", url);
            }
        }

        // 2. Data URI (Base64)
        if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var commaIndex = url.IndexOf(',');
                if (commaIndex > 0)
                {
                    var header = url[..commaIndex];
                    var base64 = url[(commaIndex + 1)..];
                    var mime = header.Split(';')[0].Replace("data:", "");
                    var bytes = Convert.FromBase64String(base64);
                    return (bytes, mime);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse data URI image");
            }
        }

        // 3. HTTP URL
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var response = await _httpClient.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    var mimeType = response.Content.Headers.ContentType?.MediaType ?? GuessMimeType(url);
                    return (bytes, mimeType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch image bytes via HTTP from {Url}", url);
            }
        }

        return null;
    }

    private static string GuessMimeType(string url) => url.ToLowerInvariant() switch
    {
        var u when u.EndsWith(".png") => "image/png",
        var u when u.EndsWith(".webp") => "image/webp",
        _ => "image/jpeg"
    };

    private async Task<(bool Success, string? Content)> CallGeminiVisionAsync(
        string prompt, byte[] imageBytes, string mimeType, CancellationToken ct)
    {
        if (!_geminiKeyPool.HasKeys)
        {
            _logger.LogWarning("No Gemini API keys configured in AiCompliance:Gemini:ApiKeys or ApiKey.");
            return (false, null);
        }

        var model = _configuration["AiCompliance:Gemini:Model"]?.Trim();
        if (string.IsNullOrWhiteSpace(model)) model = "gemini-2.0-flash";

        var maxTokensStr = _configuration["AiCompliance:Gemini:MaxOutputTokens"]?.Trim();
        var maxOutputTokens = int.TryParse(maxTokensStr, out var m) && m > 0 ? m : 4096;

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt },
                        new { inline_data = new { mime_type = mimeType, data = Convert.ToBase64String(imageBytes) } }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.1,
                maxOutputTokens = maxOutputTokens,
                responseMimeType = "application/json"
            }
        };

        var payloadJson = JsonSerializer.Serialize(payload);
        var keysToTry = _geminiKeyPool.GetAllKeysInOrder();

        for (int attempt = 0; attempt < keysToTry.Length; attempt++)
        {
            var apiKey = keysToTry[attempt];
            try
            {
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content, ct);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(ct);
                    var doc = JsonNode.Parse(responseBody);
                    var text = doc?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();
                    return (!string.IsNullOrWhiteSpace(text), text);
                }

                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Gemini Vision API call with key [..{KeySuffix}] failed with code {StatusCode}: {Error}",
                    apiKey.Length > 8 ? apiKey[^8..] : apiKey,
                    response.StatusCode,
                    error);

                // If 429 (Too Many Requests), 403 (Quota Limit), or 503 (Overloaded), try next key in pool
                if ((int)response.StatusCode == 429 || (int)response.StatusCode == 403 || (int)response.StatusCode == 503)
                {
                    if (attempt < keysToTry.Length - 1)
                    {
                        _logger.LogInformation("Failing over to next Gemini API key in pool (attempt {Attempt}/{Total})...", attempt + 2, keysToTry.Length);
                        continue;
                    }
                }

                return (false, null);
            }
            catch (Exception ex) when (attempt < keysToTry.Length - 1)
            {
                _logger.LogWarning(ex, "Exception calling Gemini Vision API with key [..{KeySuffix}], failing over to next key...", apiKey.Length > 8 ? apiKey[^8..] : apiKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception calling Gemini Vision API after trying {Total} keys.", keysToTry.Length);
                return (false, null);
            }
        }

        return (false, null);
    }

    private async Task<(bool Success, string? Content)> CallGroqChatAsync(
        string systemPrompt, string userPrompt, CancellationToken ct, bool jsonMode = true)
    {
        if (!_groqKeyPool.HasKeys)
        {
            _logger.LogWarning("No Groq API keys configured in AiCompliance:Groq:ApiKeys or ApiKey.");
            return (false, null);
        }

        var model = _configuration["AiCompliance:Groq:Model"]?.Trim();
        if (string.IsNullOrWhiteSpace(model)) model = "llama-3.3-70b-versatile";

        var maxTokensStr = _configuration["AiCompliance:Groq:MaxTokens"]?.Trim();
        var maxTokens = int.TryParse(maxTokensStr, out var m) && m > 0 ? m : 4096;

        const string url = "https://api.groq.com/openai/v1/chat/completions";

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }
            },
            ["temperature"] = 0.2,
            ["max_tokens"] = maxTokens
        };

        if (jsonMode)
        {
            payload["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        var payloadJson = payload.ToJsonString();
        var keysToTry = _groqKeyPool.GetAllKeysInOrder();

        for (int attempt = 0; attempt < keysToTry.Length; attempt++)
        {
            var apiKey = keysToTry[attempt];
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(ct);
                    var doc = JsonNode.Parse(responseBody);
                    var text = doc?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
                    return (!string.IsNullOrWhiteSpace(text), text);
                }

                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Groq API call with key [..{KeySuffix}] failed with code {StatusCode}: {Error}",
                    apiKey.Length > 8 ? apiKey[^8..] : apiKey,
                    response.StatusCode,
                    error);

                // If 429 (Too Many Requests), 403 (Quota Limit), or 503 (Overloaded), try next key in pool
                if ((int)response.StatusCode == 429 || (int)response.StatusCode == 403 || (int)response.StatusCode == 503)
                {
                    if (attempt < keysToTry.Length - 1)
                    {
                        _logger.LogInformation("Failing over to next Groq API key in pool (attempt {Attempt}/{Total})...", attempt + 2, keysToTry.Length);
                        continue;
                    }
                }

                return (false, null);
            }
            catch (Exception ex) when (attempt < keysToTry.Length - 1)
            {
                _logger.LogWarning(ex, "Exception calling Groq API with key [..{KeySuffix}], failing over to next key...", apiKey.Length > 8 ? apiKey[^8..] : apiKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception calling Groq API after trying {Total} keys.", keysToTry.Length);
                return (false, null);
            }
        }

        return (false, null);
    }
    #endregion

}

/// <summary>
/// Round-robin rotation state for one provider's key pool, thread-safe via
/// Interlocked.Increment. Deliberately NOT a nested/inline field of
/// AiComplianceService: that class is registered via AddHttpClient, whose typed
/// client has Transient lifetime by default -- a fresh instance (and a reset
/// counter) per DI resolution, which is effectively once per request. An
/// ApiKeyPool built inline in AiComplianceService's constructor would always
/// restart at key[0] on almost every call, silently defeating true rotation
/// across requests (it would only ever fail over within a single request that
/// itself makes 2+ AI calls). Registering the pools as their own Singleton and
/// injecting them keeps the counter alive for the process lifetime instead.
/// </summary>
public sealed class ApiKeyPool
{
    private readonly string[] _keys;
    private int _currentIndex = -1;

    public ApiKeyPool(IConfiguration config, string sectionPath, string singleKeyPath)
    {
        var keys = config.GetSection(sectionPath).Get<string[]>()?
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct()
            .ToArray();

        if (keys == null || keys.Length == 0)
        {
            var singleKey = config[singleKeyPath]?.Trim();
            if (!string.IsNullOrWhiteSpace(singleKey))
            {
                keys = [singleKey];
            }
        }

        _keys = keys ?? Array.Empty<string>();
    }

    public bool HasKeys => _keys.Length > 0;
    public int Count => _keys.Length;

    public string[] GetAllKeysInOrder()
    {
        if (_keys.Length == 0) return Array.Empty<string>();
        if (_keys.Length == 1) return _keys;

        var start = (int)((uint)Interlocked.Increment(ref _currentIndex) % (uint)_keys.Length);
        var result = new string[_keys.Length];
        for (int i = 0; i < _keys.Length; i++)
        {
            result[i] = _keys[(start + i) % _keys.Length];
        }
        return result;
    }
}

/// <summary>Singleton home for both providers' key pools -- see ApiKeyPool's remarks.</summary>
public sealed class AiKeyPools
{
    public ApiKeyPool Gemini { get; }
    public ApiKeyPool Groq { get; }

    public AiKeyPools(IConfiguration configuration)
    {
        Gemini = new ApiKeyPool(configuration, "AiCompliance:Gemini:ApiKeys", "AiCompliance:Gemini:ApiKey");
        Groq = new ApiKeyPool(configuration, "AiCompliance:Groq:ApiKeys", "AiCompliance:Groq:ApiKey");
    }
}
