using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Features.Notifications;
using StreetBiz.Infrastructure.Notifications.Push;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Tests.Support;

namespace StreetBiz.Infrastructure.Tests;

public sealed class PushNotificationTests
{
    private sealed class RecordingQueue : IPushQueue
    {
        public List<PushMessage> Messages { get; } = [];
        public void Enqueue(IEnumerable<PushMessage> messages) => Messages.AddRange(messages);
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTime UtcNow { get; } = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
    }

    private static Notification Note(long userId, string title = "Đơn hàng mới") => new()
    {
        user_id = userId,
        notification_type = "ORDER",
        title = title,
        body = "Bạn có đơn hàng mới.",
        related_entity_type = "ORDER",
        related_entity_id = 42,
        sent_at = DateTime.UtcNow,
    };

    private const string TokenA = "ExponentPushToken[aaaaaaaaaaaaaaaaaaaaaa]";
    private const string TokenB = "ExponentPushToken[bbbbbbbbbbbbbbbbbbbbbb]";

    [Fact]
    public async Task A_saved_notification_is_queued_with_its_id()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var queue = new RecordingQueue();
        await using var db = database.NewContext(new NotificationPushInterceptor(queue));

        db.Notifications.Add(Note(SqliteTestDatabase.VendorA));
        await db.SaveChangesAsync();

        var message = Assert.Single(queue.Messages);
        Assert.True(message.NotificationId > 0);
        Assert.Equal(SqliteTestDatabase.VendorA, message.UserId);
        Assert.Equal(("ORDER", "Đơn hàng mới", "ORDER", 42L), (message.Type, message.Title, message.EntityType, message.EntityId));
    }

    [Fact]
    public async Task Inside_a_transaction_the_push_waits_for_commit_and_a_rollback_sends_nothing()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var queue = new RecordingQueue();
        await using var db = database.NewContext(new NotificationPushInterceptor(queue));

        await using (var rolledBack = await db.Database.BeginTransactionAsync())
        {
            db.Notifications.Add(Note(SqliteTestDatabase.VendorA, "rolled back"));
            await db.SaveChangesAsync();
            await rolledBack.RollbackAsync();
        }

        db.ChangeTracker.Clear();
        Assert.Empty(queue.Messages);

        await using (var committed = await db.Database.BeginTransactionAsync())
        {
            db.Notifications.Add(Note(SqliteTestDatabase.VendorB, "committed"));
            await db.SaveChangesAsync();
            Assert.Empty(queue.Messages);
            await committed.CommitAsync();
        }

        Assert.Equal("committed", Assert.Single(queue.Messages).Title);
    }

    [Fact]
    public async Task Saving_anything_else_queues_nothing()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var queue = new RecordingQueue();
        await using var db = database.NewContext(new NotificationPushInterceptor(queue));

        var user = await db.UserAccounts.FindAsync(SqliteTestDatabase.Customer);
        user!.full_name = "Renamed";
        await db.SaveChangesAsync();

        Assert.Empty(queue.Messages);
    }

    [Fact]
    public async Task A_token_belongs_to_the_account_signed_in_on_the_phone_now()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var devices = new UserDeviceRepository(database.Db, new FixedClock());

        await devices.RegisterAsync(SqliteTestDatabase.VendorA, "install-1", "ANDROID", TokenA, default);
        // Same phone, now signed in to the customer: the vendor must stop receiving pushes there.
        await devices.RegisterAsync(SqliteTestDatabase.Customer, "install-1", "ANDROID", TokenA, default);
        await devices.RegisterAsync(SqliteTestDatabase.Customer, "install-2", "IOS", TokenB, default);

        var targets = await devices.ActiveTargetsAsync([SqliteTestDatabase.VendorA, SqliteTestDatabase.Customer], default);
        Assert.Equal(
            [(SqliteTestDatabase.Customer, TokenA), (SqliteTestDatabase.Customer, TokenB)],
            targets.Select(t => (t.UserId, t.PushToken)).OrderBy(t => t.PushToken).ToList());

        await devices.UnregisterAsync(SqliteTestDatabase.Customer, "install-2", default);
        Assert.Equal(TokenA, Assert.Single(await devices.ActiveTargetsAsync([SqliteTestDatabase.Customer], default)).PushToken);
    }

    [Fact]
    public async Task Delivery_sends_one_push_per_device_and_drops_tokens_of_removed_apps()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var devices = new UserDeviceRepository(database.Db, new FixedClock());
        await devices.RegisterAsync(SqliteTestDatabase.Customer, "install-1", "ANDROID", TokenA, default);
        await devices.RegisterAsync(SqliteTestDatabase.Customer, "install-2", "IOS", TokenB, default);

        string? sentJson = null;
        var handler = new StubHandler(async request =>
        {
            sentJson = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"status":"ok","id":"1"},{"status":"error","message":"gone","details":{"error":"DeviceNotRegistered"}}]}""",
                    Encoding.UTF8, "application/json"),
            };
        });
        var sender = new ExpoPushSender(new HttpClient(handler), Options.Create(new PushSettings()), NullLogger<ExpoPushSender>.Instance);
        var services = new ServiceCollection();
        services.AddScoped<IUserDeviceRepository>(_ => new UserDeviceRepository(database.NewContext(), new FixedClock()));
        await using var provider = services.BuildServiceProvider();
        var dispatcher = new PushDispatcherService(new PushQueue(), provider.GetRequiredService<IServiceScopeFactory>(), sender,
            Options.Create(new PushSettings()), NullLogger<PushDispatcherService>.Instance);

        await dispatcher.DeliverAsync([new PushMessage(SqliteTestDatabase.Customer, 7, "ORDER", "Đơn sẵn sàng", "Mời bạn đến lấy món.", "ORDER", 42)], default);

        using var payload = JsonDocument.Parse(sentJson!);
        var messages = payload.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("Đơn sẵn sàng", messages[0].GetProperty("title").GetString());
        Assert.Equal(7, messages[0].GetProperty("data").GetProperty("notificationId").GetInt64());
        Assert.Equal("ORDER", messages[0].GetProperty("data").GetProperty("entityType").GetString());

        // Expo answers in the order of the request; ticket 2 (whichever token was second) is gone.
        var gone = messages[1].GetProperty("to").GetString();
        var left = Assert.Single(await new UserDeviceRepository(database.NewContext(), new FixedClock())
            .ActiveTargetsAsync([SqliteTestDatabase.Customer], default));
        Assert.NotEqual(gone, left.PushToken);
    }

    [Theory]
    [InlineData("ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]", true)]
    [InlineData("ExpoPushToken[xxxxxxxxxxxxxxxxxxxxxxxxxx]", true)]
    [InlineData("fcm-raw-token-xxxxxxxxxxxxxxxxxxxxxxxxx", false)]
    [InlineData("ExponentPushToken[", false)]
    [InlineData("", false)]
    public void Only_expo_push_tokens_are_accepted(string token, bool valid) =>
        Assert.Equal(valid, PushDeviceRules.IsExpoToken(token));

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }
}
