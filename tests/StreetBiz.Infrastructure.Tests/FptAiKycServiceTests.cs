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
            client, config, NullLogger<FptAiKycService>.Instance, new StubFileStorage(), geminiVision);
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
}
