using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreetBiz.Infrastructure.Notifications;

namespace StreetBiz.Infrastructure.Tests;

public sealed class EsmsSmsSenderTests
{
    private sealed class StubHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public Dictionary<string, string>? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = JsonSerializer.Deserialize<Dictionary<string, string>>(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(status) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") };
        }
    }

    private static EsmsSmsSender Build(StubHandler handler, Action<SmsSettings>? tweak = null)
    {
        var settings = new SmsSettings { Provider = "Esms", ApiKey = "key", SecretKey = "secret" };
        tweak?.Invoke(settings);
        return new EsmsSmsSender(new HttpClient(handler), Options.Create(settings), NullLogger<EsmsSmsSender>.Instance);
    }

    [Fact]
    public async Task A_fixed_number_message_carries_both_keys_as_strings_and_no_brandname()
    {
        var handler = new StubHandler("""{"CodeResult":"100","CountRegenerate":0,"SMSID":"abc"}""");

        await Build(handler).SendAsync("0905000001", "code 123456", CancellationToken.None);

        handler.Request!.RequestUri!.ToString().Should().Be(EsmsSmsSender.DefaultEndpoint);
        handler.Body.Should().Contain("ApiKey", "key").And.Contain("SecretKey", "secret")
            .And.Contain("Phone", "0905000001").And.Contain("Content", "code 123456")
            .And.Contain("SmsType", "8").And.Contain("Sandbox", "0").And.Contain("IsUnicode", "0");
        handler.Body.Should().NotContainKey("Brandname");
    }

    [Fact]
    public async Task A_leftover_brandname_is_not_sent_with_a_fixed_number_message()
    {
        var handler = new StubHandler("""{"CodeResult":"100"}""");

        await Build(handler, s => s.Brandname = "StreetBiz").SendAsync("0905000001", "x", CancellationToken.None);

        handler.Body.Should().NotContainKey("Brandname").And.Contain("SmsType", "8");
    }

    [Fact]
    public async Task Sandbox_and_brandname_are_sent_when_configured()
    {
        var handler = new StubHandler("""{"CodeResult":"100"}""");

        await Build(handler, s => { s.Sandbox = true; s.SmsType = "2"; s.Brandname = "StreetBiz"; })
            .SendAsync("0905000001", "x", CancellationToken.None);

        handler.Body.Should().Contain("Sandbox", "1").And.Contain("SmsType", "2").And.Contain("Brandname", "StreetBiz");
    }

    [Theory]
    [InlineData("""{"CodeResult":"101","ErrorMessage":"Authorize Failed"}""")]
    [InlineData("""{"CodeResult":"103"}""")]
    [InlineData("""{"CodeResult":"140"}""")]
    [InlineData("""{"error":"Invalid request"}""")]
    [InlineData("not json")]
    public async Task Any_answer_other_than_100_is_a_failure_that_does_not_leak_the_message_or_keys(string json)
    {
        var handler = new StubHandler(json);

        var act = () => Build(handler).SendAsync("0905000001", "code 123456", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().NotContain("123456").And.NotContain("secret");
    }

    [Fact]
    public async Task A_gateway_http_error_is_a_failure()
    {
        var handler = new StubHandler("{}", HttpStatusCode.BadGateway);

        var act = () => Build(handler).SendAsync("0905000001", "x", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
