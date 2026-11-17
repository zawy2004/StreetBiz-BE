using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreetBiz.Infrastructure.Notifications;

namespace StreetBiz.Infrastructure.Tests;

public sealed class SpeedSmsSenderTests
{
    private sealed class StubHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public JsonElement Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            return new HttpResponseMessage(status) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") };
        }
    }

    private const string Success = """{"status":"success","code":"00","data":{"tranId":1,"totalSMS":1,"totalPrice":300,"invalidPhone":[]}}""";

    private static SpeedSmsSender Build(StubHandler handler, Action<SmsSettings>? tweak = null)
    {
        var settings = new SmsSettings { Provider = "SpeedSms", ApiKey = "token-123" };
        tweak?.Invoke(settings);
        return new SpeedSmsSender(new HttpClient(handler), Options.Create(settings), NullLogger<SpeedSmsSender>.Instance);
    }

    [Fact]
    public async Task The_token_goes_in_a_basic_header_and_the_body_carries_a_recipient_array()
    {
        var handler = new StubHandler(Success);

        await Build(handler).SendAsync("0905000001", "code 123456", CancellationToken.None);

        handler.Request!.RequestUri!.ToString().Should().Be(SpeedSmsSender.DefaultEndpoint);
        handler.Request.RequestUri.Query.Should().BeEmpty("the token must never be in the URL");
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("token-123:x"));
        handler.Request.Headers.Authorization!.Scheme.Should().Be("Basic");
        handler.Request.Headers.Authorization.Parameter.Should().Be(expected);
        handler.Body.GetProperty("to").EnumerateArray().Select(e => e.GetString()).Should().Equal("0905000001");
        handler.Body.GetProperty("content").GetString().Should().Be("code 123456");
        handler.Body.GetProperty("sms_type").GetInt32().Should().Be(2);
        handler.Body.GetProperty("sender").GetString().Should().BeEmpty();
        handler.Body.ToString().Should().NotContain("token-123");
    }

    [Theory]
    [InlineData("3")]
    [InlineData("5")]
    public async Task The_sender_is_sent_only_for_brandname_and_android_types(string smsType)
    {
        var handler = new StubHandler(Success);

        await Build(handler, s => { s.SmsType = smsType; s.Sender = "StreetBiz"; })
            .SendAsync("0905000001", "x", CancellationToken.None);

        handler.Body.GetProperty("sms_type").GetInt32().Should().Be(int.Parse(smsType));
        handler.Body.GetProperty("sender").GetString().Should().Be("StreetBiz");
    }

    [Fact]
    public async Task A_leftover_sender_is_not_sent_with_a_random_number_message()
    {
        var handler = new StubHandler(Success);

        await Build(handler, s => s.Sender = "StreetBiz").SendAsync("0905000001", "x", CancellationToken.None);

        handler.Body.GetProperty("sender").GetString().Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"status":"error","code":"300","message":"Not enough balance"}""")]
    [InlineData("""{"status":"error","code":"007","message":"IP locked"}""")]
    [InlineData("""{"status":"success","code":"00","data":{"invalidPhone":["0905000001"]}}""")]
    [InlineData("""{"error":"Invalid request"}""")]
    [InlineData("not json")]
    public async Task Any_answer_other_than_a_clean_success_is_a_failure_that_does_not_leak_the_message_or_token(string json)
    {
        var handler = new StubHandler(json);

        var act = () => Build(handler).SendAsync("0905000001", "code 123456", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().NotContain("123456").And.NotContain("token-123");
    }

    [Fact]
    public async Task A_gateway_http_error_is_a_failure()
    {
        var handler = new StubHandler("{}", HttpStatusCode.BadGateway);

        var act = () => Build(handler).SendAsync("0905000001", "x", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
