using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// FPT.AI's free-tier quota is small enough to exhaust during ordinary testing (confirmed
/// 2026-09-20: a burst limit of 4 req/s, 60 req/min, returning {"message":"API rate limit
/// exceeded."} with NO "errorCode"/"code" field at all). These tests guard that this specific
/// shape -- not just a network failure -- correctly falls over to Gemini instead of being
/// misread as "card unreadable" or "faces don't match".
/// </summary>
public sealed class FptAiKycServiceTests
{
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0];

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubFileStorage : StreetBiz.Application.Common.Interfaces.IFileStorage
    {
        public Task SaveAsync(string relativePath, Stream content, CancellationToken ct) => Task.CompletedTask;

        public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken ct) =>
            Task.FromResult<Stream?>(new MemoryStream(JpegBytes));

        public Task<bool> ExistsAsync(string relativePath, CancellationToken ct) => Task.FromResult(true);
    }

    private static FptAiKycService BuildService(
        HttpMessageHandler handler, Dictionary<string, string?> configValues)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        var keyPools = new AiKeyPools(config);
        var client = new HttpClient(handler);
        var geminiVision = new GeminiVisionClient(client, config, NullLogger<GeminiVisionClient>.Instance, keyPools);
        return new FptAiKycService(
            client, config, NullLogger<FptAiKycService>.Instance, new StubFileStorage(), geminiVision, keyPools);
    }

    private const string FrontUrl = "/api/uploads/evidence/10/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg";
    private const string BackUrl = "/api/uploads/evidence/10/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.jpg";
    private const string SelfieUrl = "/api/uploads/evidence/10/cccccccccccccccccccccccccccccccc.jpg";

    [Fact]
    public async Task Fpt_rate_limited_id_recognition_falls_back_to_Gemini_and_still_reads_back_side_fields()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("idr/vnm"))
            {
                // FPT's rate-limit body has no errorCode field at all -- distinct from a real
                // "card unreadable" answer, which always has errorCode/data.
                return new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("""{"message":"API rate limit exceeded."}""")
                };
            }
            if (url.Contains("googleapis.com"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"candidates":[{"content":{"parts":[{"text":"{\"idNumber\":\"048095000123\",\"fullName\":\"NGUYEN VAN A\",\"dob\":\"01/01/1990\",\"gender\":\"Nam\",\"nationality\":\"Việt Nam\",\"permanentAddress\":\"Đà Nẵng\",\"ethnicity\":\"Kinh\",\"idIssuedDate\":\"01/01/2021\",\"idIssuedPlace\":\"Cục Cảnh sát QLHC về TTXH\",\"confidencePercent\":92,\"warning\":null}"}]}}]}
                        """)
                };
            }
            throw new InvalidOperationException("Unexpected URL: " + url);
        });

        var service = BuildService(handler, new Dictionary<string, string?>
        {
            ["AiCompliance:FptAi:ApiKey"] = "test-fpt-key",
            ["AiCompliance:Gemini:ApiKey"] = "test-gemini-key",
        });

        var result = await service.ExtractIdCardAsync(FrontUrl, BackUrl, default);

        result.IsAiGenerated.Should().BeTrue();
        result.Summary.Should().Contain("Gemini Vision");
        result.IdNumber.Should().Be("048095000123");
        // Back-side-only fields: proves both images actually reached Gemini in one call.
        result.Ethnicity.Should().Be("Kinh");
        result.IdIssuedPlace.Should().Be("Cục Cảnh sát QLHC về TTXH");
    }

    [Fact]
    public async Task Fpt_rate_limited_facematch_falls_back_to_Gemini_without_claiming_a_fabricated_precise_score()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("checkface"))
            {
                return new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("""{"message":"API rate limit exceeded."}""")
                };
            }
            if (url.Contains("googleapis.com"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"candidates":[{"content":{"parts":[{"text":"{\"verdict\":\"LIKELY_SAME\",\"noFaceDetected\":false,\"reasoning\":\"Cung mat, mui, mieng tuong dong.\"}"}]}}]}
                        """)
                };
            }
            throw new InvalidOperationException("Unexpected URL: " + url);
        });

        var service = BuildService(handler, new Dictionary<string, string?>
        {
            ["AiCompliance:FptAi:FaceMatchApiKey"] = "test-fpt-facematch-key",
            ["AiCompliance:Gemini:ApiKey"] = "test-gemini-key",
        });

        var result = await service.MatchFaceAsync(SelfieUrl, FrontUrl, default);

        result.IsAiGenerated.Should().BeTrue();
        result.IsMatch.Should().BeTrue();
        result.Summary.Should().Contain("Gemini Vision");
        // Must read as a coarse AI estimate, never presented as FPT.AI's calibrated metric.
        result.Summary.Should().NotContain("FPT.AI]", "the label must make clear this came from the fallback, not the calibrated provider");
    }

    [Fact]
    public async Task Fpt_success_never_calls_Gemini_at_all()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("idr/vnm"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"errorCode":0,"errorMessage":"","data":[{"id":"048095000123","id_prob":"0.99","name":"NGUYEN VAN A","name_prob":"0.98","address":"Đà Nẵng","address_prob":"0.97","dob":"01/01/1990","dob_prob":"0.9","sex":"Nam","sex_prob":"0.9","nationality":"Việt Nam","nationality_prob":"0.9"}]}
                        """)
                };
            }
            throw new InvalidOperationException("Gemini should not be called when FPT.AI succeeds: " + url);
        });

        var service = BuildService(handler, new Dictionary<string, string?>
        {
            ["AiCompliance:FptAi:ApiKey"] = "test-fpt-key",
            ["AiCompliance:Gemini:ApiKey"] = "test-gemini-key",
        });

        var result = await service.ExtractIdCardAsync(FrontUrl, backFileUrl: null, default);

        result.Summary.Should().Contain("FPT.AI");
        result.IdNumber.Should().Be("048095000123");
    }

    // --- Key rotation: every FPT.AI key first, then Gemini ---------------------------------------

    private const string FptFrontOk = """
        {"errorCode":0,"errorMessage":"","data":[{"id":"048095000123","id_prob":"0.99","name":"NGUYEN VAN A","name_prob":"0.98","address":"Đà Nẵng","address_prob":"0.97","dob":"01/01/1990","dob_prob":"0.9","sex":"Nam","sex_prob":"0.9","nationality":"Việt Nam","nationality_prob":"0.9"}]}
        """;

    private const string FptBackOk = """
        {"errorCode":0,"errorMessage":"","data":[{"ethnicity":"N/A","ethnicity_prob":"0","issue_date":"15/06/2021","issue_date_prob":"0.95","issue_loc":"Cục Cảnh sát QLHC về TTXH","issue_loc_prob":"0.9","features":"N/A","features_prob":"0"}]}
        """;

    private const string GeminiOcrOk = """
        {"candidates":[{"content":{"parts":[{"text":"{\"idNumber\":\"048095000123\",\"fullName\":\"NGUYEN VAN A\",\"dob\":\"01/01/1990\",\"gender\":\"Nam\",\"nationality\":\"Việt Nam\",\"permanentAddress\":\"Đà Nẵng\",\"ethnicity\":null,\"idIssuedDate\":null,\"idIssuedPlace\":null,\"confidencePercent\":90,\"warning\":null}"}]}}]}
        """;

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private static string Key(HttpRequestMessage req, string header) =>
        req.Headers.TryGetValues(header, out var values) ? values.Single() : "";

    [Fact]
    public async Task An_out_of_quota_FPT_key_hands_over_to_the_next_FPT_key_before_Gemini()
    {
        var used = new List<string>();
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("googleapis.com"))
                throw new InvalidOperationException("Gemini must not be called while an FPT.AI key works");
            var key = Key(req, "api-key");
            used.Add(key);
            return key == "fpt-key-1"
                ? Json((HttpStatusCode)429, """{"message":"API rate limit exceeded."}""")
                : Json(HttpStatusCode.OK, used.Count(k => k == "fpt-key-2") == 1 ? FptFrontOk : FptBackOk);
        });
        var service = BuildService(handler, new Dictionary<string, string?>
        {
            ["AiCompliance:FptAi:ApiKeys:0"] = "fpt-key-1",
            ["AiCompliance:FptAi:ApiKeys:1"] = "fpt-key-2",
            ["AiCompliance:Gemini:ApiKey"] = "test-gemini-key",
        });

        var result = await service.ExtractIdCardAsync(FrontUrl, BackUrl, default);

        result.Summary.Should().Contain("FPT.AI");
        result.IdNumber.Should().Be("048095000123");
        result.IdIssuedPlace.Should().Be("Cục Cảnh sát QLHC về TTXH");
        // Front: key 1 refused, key 2 answered. Back starts on key 2, the one that worked.
        used.Should().Equal("fpt-key-1", "fpt-key-2", "fpt-key-2");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task A_refused_or_failing_FPT_key_also_hands_over(int status)
    {
        var used = new List<string>();
        var handler = new StubHandler(req =>
        {
            var key = Key(req, "api-key");
            used.Add(key);
            return key == "fpt-key-1"
                ? Json((HttpStatusCode)status, """{"message":"error"}""")
                : Json(HttpStatusCode.OK, FptFrontOk);
        });
        var service = BuildService(handler, new Dictionary<string, string?>
        {
            ["AiCompliance:FptAi:ApiKeys:0"] = "fpt-key-1",
            ["AiCompliance:FptAi:ApiKeys:1"] = "fpt-key-2",
        });

        var result = await service.ExtractIdCardAsync(FrontUrl, backFileUrl: null, default);

        result.Summary.Should().Contain("FPT.AI");
        used.Should().Equal("fpt-key-1", "fpt-key-2");
    }

    [Fact]
    public async Task Gemini_is_used_only_after_every_FPT_key_is_exhausted()
    {
        var fptCalls = new List<string>();
        var geminiCalls = 0;
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("googleapis.com"))
            {
                geminiCalls++;
                return Json(HttpStatusCode.OK, GeminiOcrOk);
            }
            fptCalls.Add(Key(req, "api-key"));
            return Json((HttpStatusCode)429, """{"message":"API rate limit exceeded."}""");
        });
        var service = BuildService(handler, new Dictionary<string, string?>
        {
            ["AiCompliance:FptAi:ApiKeys:0"] = "fpt-key-1",
            ["AiCompliance:FptAi:ApiKeys:1"] = "fpt-key-2",
            ["AiCompliance:FptAi:ApiKeys:2"] = "fpt-key-3",
            ["AiCompliance:Gemini:ApiKey"] = "test-gemini-key",
        });

        var result = await service.ExtractIdCardAsync(FrontUrl, backFileUrl: null, default);

        fptCalls.Should().BeEquivalentTo(["fpt-key-1", "fpt-key-2", "fpt-key-3"]);
        geminiCalls.Should().Be(1);
        result.Summary.Should().Contain("Gemini");
    }

    [Fact]
    public async Task An_image_FPT_could_not_read_is_not_retried_on_another_key()
    {
        // errorCode 3 "unable to find ID card" is an answer about the photo: another key would
        // spend credit for the same answer.
        var fptCalls = 0;
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("googleapis.com")) return Json(HttpStatusCode.OK, GeminiOcrOk);
            fptCalls++;
            return Json(HttpStatusCode.OK, """{"errorCode":3,"errorMessage":"Unable to find ID card in the image","data":[]}""");
        });
        var service = BuildService(handler, new Dictionary<string, string?>
        {
            ["AiCompliance:FptAi:ApiKeys:0"] = "fpt-key-1",
            ["AiCompliance:FptAi:ApiKeys:1"] = "fpt-key-2",
            ["AiCompliance:Gemini:ApiKey"] = "test-gemini-key",
        });

        await service.ExtractIdCardAsync(FrontUrl, backFileUrl: null, default);

        fptCalls.Should().Be(1);
    }

    [Fact]
    public async Task The_single_ApiKey_setting_still_works()
    {
        var handler = new StubHandler(req => Key(req, "api-key") == "only-key"
            ? Json(HttpStatusCode.OK, FptFrontOk)
            : throw new InvalidOperationException("unexpected key"));
        var service = BuildService(handler, new Dictionary<string, string?>
        {
            ["AiCompliance:FptAi:ApiKey"] = "only-key",
        });

        var result = await service.ExtractIdCardAsync(FrontUrl, backFileUrl: null, default);

        result.IdNumber.Should().Be("048095000123");
    }

    [Fact]
    public async Task Facematch_rotates_its_own_keys_before_Gemini()
    {
        var used = new List<string>();
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("googleapis.com"))
                throw new InvalidOperationException("Gemini must not be called while an FPT.AI key works");
            var key = Key(req, "api_key");
            used.Add(key);
            return key == "face-key-1"
                ? Json((HttpStatusCode)429, """{"message":"API rate limit exceeded."}""")
                : Json(HttpStatusCode.OK, """{"code":"200","message":"request successful.","data":{"isMatch":true,"similarity":93.5,"isBothImgIDCard":false}}""");
        });
        var service = BuildService(handler, new Dictionary<string, string?>
        {
            // The ID Recognition key must not be used for Facematch: they are separate products.
            ["AiCompliance:FptAi:ApiKey"] = "idr-key",
            ["AiCompliance:FptAi:FaceMatchApiKeys:0"] = "face-key-1",
            ["AiCompliance:FptAi:FaceMatchApiKeys:1"] = "face-key-2",
        });

        var result = await service.MatchFaceAsync(SelfieUrl, FrontUrl, default);

        result.IsMatch.Should().BeTrue();
        result.Summary.Should().Contain("FPT.AI");
        used.Should().Equal("face-key-1", "face-key-2");
    }

    // --- Accuracy and speed fixes (2026-10-16) ------------------------------------------------

    [Fact]
    public async Task Field_probabilities_given_as_percentages_are_not_multiplied_again()
    {
        // Some card types come back as "99.10" rather than "0.991"; this used to show 9910 %.
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            {"errorCode":0,"errorMessage":"","data":[{"id":"048204004499","id_prob":"99.10","name":"ĐỖ THANH TÍN","name_prob":"98.90","dob":"17/11/2004","dob_prob":"99.50","sex":"Nam","sex_prob":"99.00"}]}
            """));
        var service = BuildService(handler, new Dictionary<string, string?> { ["AiCompliance:FptAi:ApiKey"] = "k" });

        var result = await service.ExtractIdCardAsync(FrontUrl, backFileUrl: null, default);

        result.ConfidencePercent.Should().BeInRange(98, 100);
        result.Summary.Should().NotContain("9910");
    }

    [Fact]
    public async Task Out_of_transaction_hands_over_to_the_next_FPT_key()
    {
        var used = new List<string>();
        var handler = new StubHandler(req =>
        {
            var key = Key(req, "api-key");
            used.Add(key);
            return key == "fpt-key-1"
                ? Json(HttpStatusCode.OK, """{"errorCode":1,"errorMessage":"Out of transaction"}""")
                : Json(HttpStatusCode.OK, FptFrontOk);
        });
        var service = BuildService(handler, new Dictionary<string, string?>
        {
            ["AiCompliance:FptAi:ApiKeys:0"] = "fpt-key-1",
            ["AiCompliance:FptAi:ApiKeys:1"] = "fpt-key-2",
        });

        var result = await service.ExtractIdCardAsync(FrontUrl, backFileUrl: null, default);

        result.Summary.Should().Contain("FPT.AI");
        used.Should().Equal("fpt-key-1", "fpt-key-2");
    }

    [Fact]
    public async Task When_FPT_runs_out_on_the_back_Gemini_reads_the_back_and_FPT_keeps_the_front()
    {
        var fptCalls = 0;
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("googleapis.com"))
            {
                return Json(HttpStatusCode.OK, """
                    {"candidates":[{"content":{"parts":[{"text":"{\"idNumber\":\"999999999999\",\"fullName\":\"GEMINI NAME\",\"dob\":null,\"gender\":null,\"nationality\":null,\"permanentAddress\":null,\"ethnicity\":null,\"idIssuedDate\":\"12/08/2021\",\"idIssuedPlace\":\"Cục Cảnh sát QLHC về TTXH\",\"confidencePercent\":95,\"warning\":null}"}]}}]}
                    """);
            }
            // The first FPT call (front) succeeds, the second (back) finds the key out of quota.
            return ++fptCalls == 1
                ? Json(HttpStatusCode.OK, FptFrontOk)
                : Json((HttpStatusCode)429, """{"message":"API rate limit exceeded."}""");
        });
        var service = BuildService(handler, new Dictionary<string, string?>
        {
            ["AiCompliance:FptAi:ApiKey"] = "fpt-key",
            ["AiCompliance:Gemini:ApiKey"] = "gemini-key",
        });

        var result = await service.ExtractIdCardAsync(FrontUrl, BackUrl, default);

        result.IdNumber.Should().Be("048095000123");          // FPT.AI's front, not Gemini's
        result.FullName.Should().Be("NGUYEN VAN A");
        result.IdIssuedDate.Should().Be(new DateOnly(2021, 8, 12)); // Gemini's back
        result.IdIssuedPlace.Should().Be("Cục Cảnh sát QLHC về TTXH");
        result.Summary.Should().Contain("FPT.AI + Gemini");
        result.Warnings.Should().NotContain(w => w.Contains("Không đọc được ảnh mặt sau"));
    }

    [Fact]
    public async Task An_overloaded_Gemini_model_goes_straight_to_the_fallback_model_with_the_key_in_a_header()
    {
        var calls = new List<string>();
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("idr/vnm")) return Json((HttpStatusCode)429, """{"message":"API rate limit exceeded."}""");
            url.Should().NotContain("key=", "the API key must never travel in the URL");
            calls.Add($"{url.Split("/models/")[1].Split(':')[0]}|{Key(req, "x-goog-api-key")}");
            return url.Contains("/models/busy-model:")
                ? Json(HttpStatusCode.ServiceUnavailable, """{"error":{"code":503,"status":"UNAVAILABLE"}}""")
                : Json(HttpStatusCode.OK, GeminiOcrOk);
        });
        var service = BuildService(handler, new Dictionary<string, string?>
        {
            ["AiCompliance:FptAi:ApiKey"] = "fpt-key",
            ["AiCompliance:Gemini:ApiKeys:0"] = "g-key-1",
            ["AiCompliance:Gemini:ApiKeys:1"] = "g-key-2",
            ["AiCompliance:Gemini:Model"] = "busy-model",
            ["AiCompliance:Gemini:FallbackModels:0"] = "healthy-model",
        });

        var result = await service.ExtractIdCardAsync(FrontUrl, backFileUrl: null, default);

        result.Summary.Should().Contain("Gemini");
        // One 503 is enough to leave the busy model: the second key is not tried on it, no sleeping retries.
        calls.Should().HaveCount(2);
        calls[0].Should().StartWith("busy-model|");
        calls[1].Should().StartWith("healthy-model|");
    }

    [Fact]
    public async Task An_out_of_quota_Gemini_key_hands_over_to_the_next_key_on_the_same_model()
    {
        var calls = new List<string>();
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("idr/vnm")) return Json((HttpStatusCode)429, """{"message":"API rate limit exceeded."}""");
            var key = Key(req, "x-goog-api-key");
            calls.Add(key);
            return key == "g-key-1"
                ? Json((HttpStatusCode)429, """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED"}}""")
                : Json(HttpStatusCode.OK, GeminiOcrOk);
        });
        var service = BuildService(handler, new Dictionary<string, string?>
        {
            ["AiCompliance:FptAi:ApiKey"] = "fpt-key",
            ["AiCompliance:Gemini:ApiKeys:0"] = "g-key-1",
            ["AiCompliance:Gemini:ApiKeys:1"] = "g-key-2",
        });

        var result = await service.ExtractIdCardAsync(FrontUrl, backFileUrl: null, default);

        result.Summary.Should().Contain("Gemini");
        calls.Should().BeEquivalentTo(["g-key-1", "g-key-2"]);
    }

    [Fact]
    public async Task A_Gemini_id_number_that_is_not_12_digits_is_flagged()
    {
        var handler = new StubHandler(req => req.RequestUri!.ToString().Contains("idr/vnm")
            ? Json((HttpStatusCode)429, """{"message":"API rate limit exceeded."}""")
            : Json(HttpStatusCode.OK, GeminiOcrOk.Replace("048095000123", "04809500012")));
        var service = BuildService(handler, new Dictionary<string, string?>
        {
            ["AiCompliance:FptAi:ApiKey"] = "fpt-key",
            ["AiCompliance:Gemini:ApiKey"] = "g-key",
        });

        var result = await service.ExtractIdCardAsync(FrontUrl, backFileUrl: null, default);

        result.Warnings.Should().Contain(w => w.Contains("12 chữ số"));
        result.NeedsManualVerification.Should().BeTrue();
    }

    private sealed class AsyncStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request, ct);
    }

    [Fact]
    public async Task A_hung_Gemini_model_is_left_after_the_attempt_timeout_and_skipped_on_the_next_request()
    {
        var calls = new List<string>();
        var handler = new AsyncStubHandler(async (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("idr/vnm")) return Json((HttpStatusCode)429, """{"message":"API rate limit exceeded."}""");
            var model = url.Split("/models/")[1].Split(':')[0];
            calls.Add(model);
            if (model == "hung-model")
            {
                await Task.Delay(Timeout.Infinite, ct); // never answers
            }
            return Json(HttpStatusCode.OK, GeminiOcrOk);
        });
        var service = BuildService(handler, new Dictionary<string, string?>
        {
            ["AiCompliance:FptAi:ApiKey"] = "fpt-key",
            ["AiCompliance:Gemini:ApiKey"] = "g-key",
            ["AiCompliance:Gemini:Model"] = "hung-model",
            ["AiCompliance:Gemini:FallbackModels:0"] = "healthy-model",
            ["AiCompliance:Gemini:AttemptTimeoutSeconds"] = "1",
        });

        var first = await service.ExtractIdCardAsync(FrontUrl, backFileUrl: null, default);
        var started = DateTime.UtcNow;
        var second = await service.ExtractIdCardAsync(FrontUrl, backFileUrl: null, default);

        first.Summary.Should().Contain("Gemini");
        second.Summary.Should().Contain("Gemini");
        calls.Should().Equal("hung-model", "healthy-model", "healthy-model"); // the hung model is not retried
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(1));
    }
}
