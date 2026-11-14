using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Features.Notifications;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Notifications.Push;

public sealed class PushSettings
{
    public const string SectionName = "Push";

    /// <summary>Off switch. With it on, nothing is sent until a phone registers a token.</summary>
    public bool Enabled { get; set; } = true;

    public string Endpoint { get; set; } = "https://exp.host/--/api/v2/push/send";

    /// <summary>Optional: required only when "enhanced push security" is turned on for the Expo project.</summary>
    public string? ExpoAccessToken { get; set; }
}

/// <summary>Bounded in-memory queue between the request that saved a notification and the sender.</summary>
public sealed class PushQueue : IPushQueue
{
    // A burst beyond this (a sweep notifying thousands at once) drops the oldest pushes, never
    // the request: the notification itself is already saved and shows in the app's list.
    private readonly Channel<PushMessage> channel = Channel.CreateBounded<PushMessage>(
        new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public ChannelReader<PushMessage> Reader => channel.Reader;

    public void Enqueue(IEnumerable<PushMessage> messages)
    {
        foreach (var message in messages)
        {
            channel.Writer.TryWrite(message);
        }
    }
}

/// <summary>
/// Pushes every Notifications row once it is really saved. All 30-odd places that write a
/// notification go through EF, so one interceptor covers them: rows are captured while saving,
/// read after the save (when their id exists), and queued at once, or, inside an explicit
/// transaction, only when it commits. A rolled-back transaction pushes nothing.
/// Stateless singleton; the per-context state lives in a weak table keyed by the DbContext.
/// </summary>
public sealed class NotificationPushInterceptor(IPushQueue queue) : SaveChangesInterceptor, IDbTransactionInterceptor
{
    private sealed class State
    {
        public List<Notification> Saving { get; } = [];
        public List<PushMessage> AwaitingCommit { get; } = [];
    }

    private readonly ConditionalWeakTable<DbContext, State> states = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Saved(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Saved(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Forget(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Commit(eventData.Context);

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Commit(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Discard(eventData.Context);

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => Discard(eventData.Context);

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    private void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var added = context.ChangeTracker.Entries<Notification>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToList();
        if (added.Count > 0)
        {
            states.GetOrCreateValue(context).Saving.AddRange(added);
        }
    }

    private void Saved(DbContext? context)
    {
        if (context is null || !states.TryGetValue(context, out var state) || state.Saving.Count == 0)
        {
            return;
        }

        var messages = state.Saving
            .Where(n => n.notification_id > 0)
            .Select(n => new PushMessage(n.user_id, n.notification_id, n.notification_type, n.title, n.body,
                n.related_entity_type, n.related_entity_id))
            .ToList();
        state.Saving.Clear();

        if (context.Database.CurrentTransaction is null)
        {
            queue.Enqueue(messages);
        }
        else
        {
            state.AwaitingCommit.AddRange(messages);
        }
    }

    private void Forget(DbContext? context)
    {
        if (context is not null && states.TryGetValue(context, out var state))
        {
            state.Saving.Clear();
        }
    }

    private void Commit(DbContext? context)
    {
        if (context is null || !states.TryGetValue(context, out var state) || state.AwaitingCommit.Count == 0)
        {
            return;
        }

        queue.Enqueue(state.AwaitingCommit.ToList());
        state.AwaitingCommit.Clear();
    }

    private void Discard(DbContext? context)
    {
        if (context is not null && states.TryGetValue(context, out var state))
        {
            state.Saving.Clear();
            state.AwaitingCommit.Clear();
        }
    }

    // The remaining IDbTransactionInterceptor members only observe; nothing to do.
    public InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result) => result;
    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result) => result;
    public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default) => ValueTask.FromResult(result);
    public ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default) => ValueTask.FromResult(result);
    public DbTransaction TransactionUsed(DbConnection connection, TransactionEventData eventData, DbTransaction result) => result;
    public ValueTask<DbTransaction> TransactionUsedAsync(DbConnection connection, TransactionEventData eventData, DbTransaction result, CancellationToken cancellationToken = default) => ValueTask.FromResult(result);
    public InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result) => result;
    public ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) => ValueTask.FromResult(result);
    public InterceptionResult TransactionRollingBack(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result) => result;
    public ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) => ValueTask.FromResult(result);
    public InterceptionResult CreatingSavepoint(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result) => result;
    public void CreatedSavepoint(DbTransaction transaction, TransactionEventData eventData) { }
    public ValueTask<InterceptionResult> CreatingSavepointAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) => ValueTask.FromResult(result);
    public Task CreatedSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public InterceptionResult RollingBackToSavepoint(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result) => result;
    public void RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) { }
    public ValueTask<InterceptionResult> RollingBackToSavepointAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) => ValueTask.FromResult(result);
    public Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public InterceptionResult ReleasingSavepoint(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result) => result;
    public void ReleasedSavepoint(DbTransaction transaction, TransactionEventData eventData) { }
    public ValueTask<InterceptionResult> ReleasingSavepointAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) => ValueTask.FromResult(result);
    public Task ReleasedSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Sends pushes through Expo's push service (which forwards to FCM and APNs).</summary>
public sealed class ExpoPushSender(HttpClient http, IOptions<PushSettings> options, ILogger<ExpoPushSender> logger)
{
    /// <summary>Expo accepts at most 100 messages per request.</summary>
    public const int BatchSize = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Sends one batch and returns the tokens Expo says are no longer registered.</summary>
    public async Task<IReadOnlyList<string>> SendAsync(
        IReadOnlyList<(PushTarget Target, PushMessage Message)> batch, CancellationToken cancellationToken)
    {
        var payload = batch.Select(x => new ExpoMessage(
            x.Target.PushToken,
            x.Message.Title,
            x.Message.Body,
            new Dictionary<string, object?>
            {
                ["notificationId"] = x.Message.NotificationId,
                ["type"] = x.Message.Type,
                ["entityType"] = x.Message.EntityType,
                ["entityId"] = x.Message.EntityId,
            })).ToList();

        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.Endpoint)
        {
            Content = JsonContent.Create(payload, options: Json),
        };
        if (!string.IsNullOrWhiteSpace(options.Value.ExpoAccessToken))
        {
            request.Headers.Authorization = new("Bearer", options.Value.ExpoAccessToken);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Expo push answered {Status}; {Count} push(es) not delivered.", (int)response.StatusCode, batch.Count);
            return [];
        }

        var result = await response.Content.ReadFromJsonAsync<ExpoResponse>(Json, cancellationToken);
        var gone = new List<string>();
        var tickets = result?.Data ?? [];
        for (var i = 0; i < tickets.Count && i < batch.Count; i++)
        {
            if (tickets[i].Status != "error")
            {
                continue;
            }

            if (tickets[i].Details?.Error == "DeviceNotRegistered")
            {
                gone.Add(batch[i].Target.PushToken);
            }
            else
            {
                logger.LogWarning("Expo push error for notification {NotificationId}: {Error}",
                    batch[i].Message.NotificationId, tickets[i].Details?.Error ?? tickets[i].Message);
            }
        }

        return gone;
    }

    private sealed record ExpoMessage(string To, string Title, string Body, Dictionary<string, object?> Data)
    {
        public string Sound { get; } = "default";
        public string ChannelId { get; } = "default";
        public string Priority { get; } = "high";
    }

    private sealed record ExpoResponse(List<ExpoTicket>? Data);

    private sealed record ExpoTicket(string Status, string? Message, ExpoTicketDetails? Details);

    private sealed record ExpoTicketDetails(string? Error);
}

/// <summary>
/// Drains <see cref="PushQueue"/>: looks up the recipients' active devices, sends in batches of
/// 100 and deactivates tokens of phones that removed the app. A failure is logged and dropped;
/// the notification is still in the app's list.
/// </summary>
public sealed class PushDispatcherService(
    PushQueue queue,
    IServiceScopeFactory scopes,
    ExpoPushSender sender,
    IOptions<PushSettings> options,
    ILogger<PushDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                var messages = new List<PushMessage>();
                while (messages.Count < 500 && reader.TryRead(out var message))
                {
                    messages.Add(message);
                }

                if (options.Value.Enabled && messages.Count > 0)
                {
                    await DeliverAsync(messages, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task DeliverAsync(IReadOnlyList<PushMessage> messages, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var devices = scope.ServiceProvider.GetRequiredService<IUserDeviceRepository>();
            var targets = await devices.ActiveTargetsAsync(messages.Select(m => m.UserId).Distinct().ToList(), cancellationToken);
            if (targets.Count == 0)
            {
                return;
            }

            var byUser = targets.ToLookup(t => t.UserId);
            var pairs = messages.SelectMany(m => byUser[m.UserId].Select(t => (Target: t, Message: m))).ToList();
            var gone = new List<string>();
            foreach (var batch in pairs.Chunk(ExpoPushSender.BatchSize))
            {
                gone.AddRange(await sender.SendAsync(batch, cancellationToken));
            }

            if (gone.Count > 0)
            {
                await devices.DeactivateTokensAsync(gone, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Push delivery failed for {Count} notification(s).", messages.Count);
        }
    }
}

public sealed class UserDeviceRepository(StreetBizDbContext db, IDateTimeProvider clock) : IUserDeviceRepository
{
    public async Task RegisterAsync(long userId, string deviceIdentifier, string platform, string pushToken, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        // UQ_UserDevices_PushToken: a token lives on one row only. Any other row holding it (the
        // phone's previous account, or this account's old install id) gives it up.
        await db.UserDevices
            .Where(d => d.push_token == pushToken && !(d.user_id == userId && d.device_identifier == deviceIdentifier))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.is_active, false)
                .SetProperty(d => d.push_token, (string?)null), cancellationToken);

        var device = await db.UserDevices.FirstOrDefaultAsync(
            d => d.user_id == userId && d.device_identifier == deviceIdentifier, cancellationToken);
        if (device is null)
        {
            db.UserDevices.Add(new UserDevice
            {
                user_id = userId,
                device_identifier = deviceIdentifier,
                platform = platform,
                push_token = pushToken,
                is_active = true,
                last_seen_at = now,
                created_at = now,
            });
        }
        else
        {
            device.platform = platform;
            device.push_token = pushToken;
            device.is_active = true;
            device.last_seen_at = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public Task UnregisterAsync(long userId, string deviceIdentifier, CancellationToken cancellationToken) =>
        db.UserDevices
            .Where(d => d.user_id == userId && d.device_identifier == deviceIdentifier)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.is_active, false), cancellationToken);

    public async Task<IReadOnlyList<PushTarget>> ActiveTargetsAsync(IReadOnlyCollection<long> userIds, CancellationToken cancellationToken) =>
        await db.UserDevices.AsNoTracking()
            .Where(d => userIds.Contains(d.user_id) && d.is_active && d.push_token != null)
            .Select(d => new PushTarget(d.user_id, d.push_token!))
            .ToListAsync(cancellationToken);

    public Task DeactivateTokensAsync(IReadOnlyCollection<string> pushTokens, CancellationToken cancellationToken) =>
        db.UserDevices
            .Where(d => d.push_token != null && pushTokens.Contains(d.push_token))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.is_active, false)
                .SetProperty(d => d.push_token, (string?)null), cancellationToken);
}
