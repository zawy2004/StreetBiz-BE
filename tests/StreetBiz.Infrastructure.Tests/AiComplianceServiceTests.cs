using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StreetBiz.Application.Features.WardCompliance;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// A prior draft of this service (a) compared a phone number against "declared
/// CCCD", (b) sent a bare image URL inside a text-only Gemini prompt without
/// ever attaching the image, and (c) let the LLM invent Nghi dinh/Dieu/Khoan
/// citations from scratch. These tests guard the fixes for all three.
/// </summary>
public sealed class AiComplianceServiceTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return respond(request);
        }
    }

    private static AiComplianceService BuildService(StubHandler handler, string? geminiKey = "test-key")
    {
        var client = new HttpClient(handler);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(geminiKey is null
                ? []
                : new Dictionary<string, string?> { ["AiCompliance:Gemini:ApiKey"] = geminiKey })
            .Build();
        var keyPools = new AiKeyPools(config);
        var geminiVision = new GeminiVisionClient(client, config, NullLogger<GeminiVisionClient>.Instance, keyPools);
        return new AiComplianceService(client, config, NullLogger<AiComplianceService>.Instance, keyPools, geminiVision);
    }

    [Fact]
    public async Task No_API_key_configured_returns_an_honest_non_AI_result_not_a_guess()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Should never call out with no key configured"));
        var service = BuildService(handler, geminiKey: null);

        var evidence = new[] { new WardEvidenceDto(1, "ID_CARD", "CCCD mặt trước", "https://files.test/id.jpg") };
        var result = await service.ExtractIdDocumentAsync(evidence, default);

        result.IsAiGenerated.Should().BeFalse();
        result.IdNumber.Should().BeNull("a failed OCR must never fabricate a plausible-looking ID number");
        result.ConfidencePercent.Should().Be(0);
    }

    [Fact]
    public async Task Vision_call_sends_the_actual_image_bytes_not_just_a_URL_string()
    {
        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // minimal JPEG-ish header
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("files.test"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageBytes) { Headers = { ContentType = new("image/jpeg") } }
                };
            }

            // The Gemini call itself.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"candidates":[{"content":{"parts":[{"text":"{\"detectedEncroachment\":false,\"encroachmentDistanceCm\":0,\"analysis\":\"OK\",\"visualCues\":[]}"}]}}]}
                    """)
            };
        });
        var service = BuildService(handler);

        var result = await service.AnalyzeInspectionPhotoAsync("https://files.test/photo.jpg", 2, 3, default);

        result.IsAiGenerated.Should().BeTrue();
        handler.LastRequestBody.Should().NotBeNull();
        // The critical regression check: the request to Gemini must carry the
        // actual base64 image bytes as inline_data, not merely the photo URL
        // typed into the prompt text.
        handler.LastRequestBody!.Should().Contain("inline_data");
        handler.LastRequestBody!.Should().Contain(Convert.ToBase64String(imageBytes));
    }

    [Fact]
    public async Task Unreachable_photo_falls_back_honestly_and_is_not_labelled_AI()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = BuildService(handler);

        var result = await service.AnalyzeInspectionPhotoAsync("https://files.test/missing.jpg", 2, 3, default);

        result.IsAiGenerated.Should().BeFalse();
        result.Analysis.Should().StartWith("[Hệ thống");
        result.Analysis.Should().NotContain("[AI]");
    }

    [Fact]
    public async Task Legal_draft_never_invents_a_citation_it_only_uses_the_wards_configured_schedule()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Classification must not call an LLM for a small fixed set"));
        var service = BuildService(handler);

        var schedules = new[]
        {
            new PenaltyScheduleItemDto(1, "HYGIENE_LITTERING", "Xả rác", 1500000, "Nghị định 45/2022/NĐ-CP"),
            new PenaltyScheduleItemDto(2, "UNAUTHORIZED_BUSINESS_USE", "Lấn chiếm", 2500000, "Nghị định 168/2024/NĐ-CP"),
        };

        var result = await service.ClassifyAndDraftAsync("Bày bán hàng lấn chiếm ra ngoài vạch sơn quy định", schedules, default);

        result.ViolationType.Should().Be("UNAUTHORIZED_BUSINESS_USE");
        result.LegalBasis.Should().Be("Nghị định 168/2024/NĐ-CP");
        result.SuggestedPenaltyAmount.Should().Be(2500000);
        result.HanhViViPham.Should().Contain("Lấn chiếm");
        result.BienPhapKhacPhuc.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Even_when_Groq_proposes_its_own_citation_text_the_DB_row_wins()
    {
        // Groq is configured and returns a scheduleId plus its own made-up-sounding wording --
        // this must never leak into LegalBasis/SuggestedPenaltyAmount. A prior draft did
        // `legalBasis ?? matched.LegalBasis`, which let the model's own citation text through
        // whenever it returned one at all.
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("groq.com"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"choices":[{"message":{"content":"{\"scheduleId\":2,\"hanhViViPham\":\"Bay ban lan chiem\",\"bienPhapKhacPhuc\":\"Thu don ngay\"}"}}]}
                        """)
                };
            }
            throw new InvalidOperationException("Unexpected call: " + req.RequestUri);
        });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AiCompliance:Groq:ApiKey"] = "test-groq-key" })
            .Build();
        var groqKeyPools = new AiKeyPools(config);
        var service = new AiComplianceService(
            new HttpClient(handler), config, NullLogger<AiComplianceService>.Instance, groqKeyPools,
            new GeminiVisionClient(new HttpClient(handler), config, NullLogger<GeminiVisionClient>.Instance, groqKeyPools));

        var schedules = new[]
        {
            new PenaltyScheduleItemDto(1, "HYGIENE_LITTERING", "Xả rác", 1500000, "Nghị định 45/2022/NĐ-CP"),
            new PenaltyScheduleItemDto(2, "UNAUTHORIZED_BUSINESS_USE", "Lấn chiếm", 2500000, "Nghị định 168/2024/NĐ-CP"),
        };

        var result = await service.ClassifyAndDraftAsync("Bày bán hàng lấn chiếm", schedules, default);

        result.IsAiGenerated.Should().BeTrue();
        result.LegalBasis.Should().Be("Nghị định 168/2024/NĐ-CP", "the citation must always be the DB row's, never model output");
        result.SuggestedPenaltyAmount.Should().Be(2500000);
    }

    [Fact]
    public async Task No_schedule_configured_yet_is_reported_honestly_instead_of_guessing_a_law()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Must not call out when there is nothing to classify against"));
        var service = BuildService(handler);

        var result = await service.ClassifyAndDraftAsync("Vi phạm gì đó", Array.Empty<PenaltyScheduleItemDto>(), default);

        result.IsAiGenerated.Should().BeFalse();
        result.LegalBasis.Should().BeNull();
        result.SuggestedPenaltyAmount.Should().BeNull();
    }

    [Fact]
    public void Key_pool_rotation_survives_across_service_instances()
    {
        // AddHttpClient's typed client is Transient -- a new AiComplianceService (and, before
        // the fix, a new inline ApiKeyPool with a reset counter) is created roughly once per
        // request. AiKeyPools must be the Singleton holding rotation state instead, or every
        // request effectively always tries key[0] first and the "3x quota" pooling is fake.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiCompliance:Gemini:ApiKeys:0"] = "key-A",
                ["AiCompliance:Gemini:ApiKeys:1"] = "key-B",
                ["AiCompliance:Gemini:ApiKeys:2"] = "key-C",
            })
            .Build();

        // One long-lived AiKeyPools (as DI would provide via AddSingleton<AiKeyPools>()),
        // shared by three separate "request-scoped" AiComplianceService instances.
        var sharedPools = new AiKeyPools(config);
        var handler = new StubHandler(_ => throw new InvalidOperationException("not called"));
        GeminiVisionClient GeminiFor(HttpMessageHandler h) =>
            new(new HttpClient(h), config, NullLogger<GeminiVisionClient>.Instance, sharedPools);
        _ = new AiComplianceService(new HttpClient(handler), config, NullLogger<AiComplianceService>.Instance, sharedPools, GeminiFor(handler));
        _ = new AiComplianceService(new HttpClient(handler), config, NullLogger<AiComplianceService>.Instance, sharedPools, GeminiFor(handler));
        _ = new AiComplianceService(new HttpClient(handler), config, NullLogger<AiComplianceService>.Instance, sharedPools, GeminiFor(handler));

        var firstTriesAcrossThreeRequests = new[]
        {
            sharedPools.Gemini.GetAllKeysInOrder()[0],
            sharedPools.Gemini.GetAllKeysInOrder()[0],
            sharedPools.Gemini.GetAllKeysInOrder()[0],
        };

        firstTriesAcrossThreeRequests.Distinct().Should().HaveCount(3, "three consecutive calls on a shared pool must rotate through all three keys, not repeat key[0]");
    }

    [Fact]
    public async Task Fpt_AI_success_returns_Fpt_result_and_skips_Gemini()
    {
        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("files.test"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageBytes) { Headers = { ContentType = new("image/jpeg") } }
                };
            }
            if (req.RequestUri!.ToString().Contains("fpt.ai"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {
                          "errorCode": 0,
                          "errorMessage": "",
                          "data": [{
                            "id": "048095000123",
                            "id_prob": "99.0",
                            "name": "NGUYỄN VĂN A",
                            "name_prob": "98.0",
                            "address": "Đà Nẵng",
                            "address_prob": "96.0"
                          }]
                        }
                        """)
                };
            }
            throw new InvalidOperationException("Gemini should not be called when FPT succeeds!");
        });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiCompliance:FptAi:ApiKey"] = "test-fpt-key",
                ["AiCompliance:Gemini:ApiKey"] = "test-gemini-key"
            })
            .Build();

        var fptOkKeyPools = new AiKeyPools(config);
        var service = new AiComplianceService(
            new HttpClient(handler), config, NullLogger<AiComplianceService>.Instance, fptOkKeyPools,
            new GeminiVisionClient(new HttpClient(handler), config, NullLogger<GeminiVisionClient>.Instance, fptOkKeyPools));
        var evidence = new[] { new WardEvidenceDto(1, "ID_CARD", "CCCD", "https://files.test/id.jpg") };

        var result = await service.ExtractIdDocumentAsync(evidence, default);

        result.IsAiGenerated.Should().BeTrue();
        result.Source.Should().Be("FPT.AI");
        result.IdNumber.Should().Be("048095000123");
        result.FullName.Should().Be("NGUYỄN VĂN A");
    }

    [Fact]
    public async Task Fpt_AI_out_of_tokens_falls_back_to_Gemini()
    {
        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("files.test"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(imageBytes) { Headers = { ContentType = new("image/jpeg") } }
                };
            }
            if (req.RequestUri!.ToString().Contains("fpt.ai"))
            {
                // errorCode 1 = Out of transaction / hết token
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {
                          "errorCode": 1,
                          "errorMessage": "Out of transaction"
                        }
                        """)
                };
            }
            if (req.RequestUri!.ToString().Contains("googleapis.com"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"candidates":[{"content":{"parts":[{"text":"{\"idNumber\":\"048095000999\",\"fullName\":\"TRAN VAN B\",\"address\":\"Hai Chau, Da Nang\",\"confidencePercent\":94,\"tamperingAlert\":null}"}]}}]}
                        """)
                };
            }
            throw new InvalidOperationException("Unexpected URL: " + req.RequestUri);
        });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiCompliance:FptAi:ApiKey"] = "test-fpt-key",
                ["AiCompliance:Gemini:ApiKey"] = "test-gemini-key"
            })
            .Build();

        var fptFallbackKeyPools = new AiKeyPools(config);
        var service = new AiComplianceService(
            new HttpClient(handler), config, NullLogger<AiComplianceService>.Instance, fptFallbackKeyPools,
            new GeminiVisionClient(new HttpClient(handler), config, NullLogger<GeminiVisionClient>.Instance, fptFallbackKeyPools));
        var evidence = new[] { new WardEvidenceDto(1, "ID_CARD", "CCCD", "https://files.test/id.jpg") };

        var result = await service.ExtractIdDocumentAsync(evidence, default);

        result.IsAiGenerated.Should().BeTrue();
        result.Source.Should().Be("Gemini Vision");
        result.IdNumber.Should().Be("048095000999");
        result.FullName.Should().Be("TRAN VAN B");
        result.ConfidencePercent.Should().Be(94);
    }
}
