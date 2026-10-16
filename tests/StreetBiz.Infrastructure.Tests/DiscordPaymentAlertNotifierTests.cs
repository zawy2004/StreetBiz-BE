using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Infrastructure.Notifications;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>Payment alerts posted to a Discord channel webhook, with Discord faked at the HTTP layer.</summary>
public sealed class DiscordPaymentAlertNotifierTests
{
    private const string WebhookUrl = "https://discord.com/api/webhooks/1/test-token";

    private static readonly PaymentAlert Surplus = new(
        "SEPAY", "SURPLUS", "Tiền về cho khoản đã thanh toán.", "SBP42", 900_000m, "92705", 42, 7);

    [Fact]
    public async Task An_alert_is_posted_as_one_embed_with_the_facts_a_person_needs()
    {
        var discord = new FakeDiscord(HttpStatusCode.NoContent);

        await Notifier(discord, mention: "<@&123>").NotifyAsync(Surplus, default);

        Assert.Equal(WebhookUrl, discord.LastUri);
        using var body = JsonDocument.Parse(discord.LastBody!);
        var root = body.RootElement;
        Assert.Equal("<@&123>", root.GetProperty("content").GetString());
        var embed = root.GetProperty("embeds")[0];
        Assert.Equal("Thanh toán cần xử lý: SEPAY · SURPLUS", embed.GetProperty("title").GetString());
        Assert.Equal("Tiền về cho khoản đã thanh toán.", embed.GetProperty("description").GetString());
        var fields = embed.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("value").GetString());
        Assert.Equal("SBP42", fields["Mã thanh toán"]);
        Assert.Equal("900.000 đ", fields["Số tiền"]);
        Assert.Equal("92705", fields["Mã GD nhà cung cấp"]);
        Assert.Equal("#42", fields["Giao dịch StreetBiz"]);
        Assert.Equal("Development", fields["Môi trường"]);
    }

    [Fact]
    public async Task An_empty_field_is_sent_as_a_dash_because_discord_refuses_empty_values()
    {
        var discord = new FakeDiscord(HttpStatusCode.NoContent);

        await Notifier(discord).NotifyAsync(Surplus with { ProviderReference = null, TransactionId = null }, default);

        using var body = JsonDocument.Parse(discord.LastBody!);
        Assert.All(body.RootElement.GetProperty("embeds")[0].GetProperty("fields").EnumerateArray(),
            f => Assert.False(string.IsNullOrEmpty(f.GetProperty("value").GetString())));
    }

    [Fact]
    public async Task Without_a_webhook_url_nothing_is_sent()
    {
        var discord = new FakeDiscord(HttpStatusCode.NoContent);

        await Notifier(discord, url: "").NotifyAsync(Surplus, default);

        Assert.Null(discord.LastUri);
    }

    [Fact]
    public async Task A_failing_discord_never_fails_the_payment_callback()
    {
        await Notifier(new FakeDiscord(HttpStatusCode.TooManyRequests)).NotifyAsync(Surplus, default);
        await Notifier(new FakeDiscord(throws: true)).NotifyAsync(Surplus, default);
    }

    private static DiscordPaymentAlertNotifier Notifier(FakeDiscord discord, string url = WebhookUrl, string? mention = null)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns("Development");
        return new DiscordPaymentAlertNotifier(
            new HttpClient(discord),
            Options.Create(new AlertSettings { DiscordWebhookUrl = url, DiscordMention = mention }),
            environment.Object,
            TimeProvider.System,
            NullLogger<DiscordPaymentAlertNotifier>.Instance);
    }

    private sealed class FakeDiscord(HttpStatusCode status = HttpStatusCode.NoContent, bool throws = false) : HttpMessageHandler
    {
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (throws)
            {
                throw new HttpRequestException("Discord is unreachable");
            }

            LastUri = request.RequestUri!.ToString();
            LastBody = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent("", Encoding.UTF8) };
        }
    }
}
