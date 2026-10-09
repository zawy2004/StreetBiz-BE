using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class ChatbotStore(StreetBizDbContext db, TimeProvider clock, ChatbotSettings settings) : IChatbotStore
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static DateTimeOffset Local(DateTime value) => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToOffset(TimeSpan.FromHours(7));
    private IQueryable<ChatbotConversationRecord> Owned(ChatbotActor actor) => db.ChatbotConversations.AsNoTracking()
        .Where(c => c.owner_user_id == actor.UserId && c.scope == actor.Scope && c.deleted_at == null
            && c.updated_at > Now.AddDays(-settings.RetentionDays));
    private static ChatbotConversation Dto(ChatbotConversationRecord c) => new(c.conversation_id, c.title, Local(c.created_at), Local(c.updated_at), c.active_message_id);
    private static ChatbotMessage Dto(ChatbotMessageRecord m) => ChatbotJson.Read<ChatbotMessage>(m.payload_json) with {
        Status = m.status, Version = m.version,
        CompletedAt = m.status == "GENERATING" ? null : Local(m.updated_at)
    };

    public async Task<ChatbotConversation> CreateAsync(ChatbotActor actor, string requestId, CancellationToken ct)
    {
        if (!Guid.TryParse(requestId, out var guid)) throw new ChatbotException(400, "invalid_request", "Mã yêu cầu không hợp lệ.");
        requestId = guid.ToString("D");
        var existing = await Owned(actor).SingleOrDefaultAsync(c => c.create_request_id == requestId, ct);
        if (existing is not null) return Dto(existing);
        var record = new ChatbotConversationRecord { conversation_id = Guid.NewGuid().ToString("N"), owner_user_id = actor.UserId,
            create_request_id = requestId, scope = actor.Scope, created_at = Now, updated_at = Now, actor_id = actor.UserId, version = 1 };
        db.ChatbotConversations.Add(record);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(record).State = EntityState.Detached;
            existing = await Owned(actor).SingleOrDefaultAsync(c => c.create_request_id == requestId, ct);
            if (existing is null) throw;
            return Dto(existing);
        }
        return Dto(record);
    }

    public async Task<ChatbotPage<ChatbotConversation>> ListAsync(ChatbotActor actor, string? before, CancellationToken ct)
    {
        var query = Owned(actor);
        // Creation time is immutable, so new turns do not move rows across a pagination boundary.
        if (!string.IsNullOrEmpty(before))
        {
            var cursor = await Owned(actor).SingleOrDefaultAsync(c => c.conversation_id == before, ct)
                ?? throw ChatbotException.Unavailable();
            query = query.Where(c => c.created_at < cursor.created_at || (c.created_at == cursor.created_at && string.Compare(c.conversation_id, before) < 0));
        }
        var rows = await query.OrderByDescending(c => c.created_at).ThenByDescending(c => c.conversation_id).Take(21).ToListAsync(ct);
        return new(rows.Take(20).Select(Dto).ToArray(), rows.Count > 20 ? rows[19].conversation_id : null);
    }

    public async Task<ChatbotConversation> RequireAsync(ChatbotActor actor, string id, CancellationToken ct)
    {
        var row = await Owned(actor).SingleOrDefaultAsync(c => c.conversation_id == id, ct) ?? throw ChatbotException.Unavailable();
        if (row.active_message_id is { } stale && row.lease_until <= Now)
        {
            await db.ChatbotMessages.Where(m => m.message_id == stale && m.status == "GENERATING")
                .ExecuteUpdateAsync(p => p.SetProperty(m => m.status, "INTERRUPTED").SetProperty(m => m.updated_at, Now)
                    .SetProperty(m => m.version, m => m.version + 1)
                    .SetProperty(m => m.input_tokens, m => m.input_tokens + m.reserved_tokens).SetProperty(m => m.reserved_tokens, 0), ct);
            await db.ChatbotConversations.Where(c => c.conversation_id == id && c.active_message_id == stale && c.lease_until <= Now)
                .ExecuteUpdateAsync(p => p.SetProperty(c => c.active_message_id, (string?)null).SetProperty(c => c.lease_until, (DateTime?)null)
                    .SetProperty(c => c.version, c => c.version + 1).SetProperty(c => c.actor_id, actor.UserId), ct);
            await AuditToolAsync(actor, stale, "recovery", "INTERRUPTED", ct);
            row.active_message_id = null;
        }
        return Dto(row);
    }

    public async Task<ChatbotPage<ChatbotMessage>> HistoryAsync(ChatbotActor actor, string id, long? before, CancellationToken ct)
    {
        await RequireAsync(actor, id, ct);
        var rows = await db.ChatbotMessages.AsNoTracking().Where(m => m.conversation_id == id && (!before.HasValue || m.ordinal < before))
            .OrderByDescending(m => m.ordinal).Take(31).ToListAsync(ct);
        return new(rows.Take(30).Reverse().Select(Dto).ToArray(), rows.Count > 30 ? rows[29].ordinal.ToString() : null);
    }

    public async Task<ChatbotMessage> MessageAsync(ChatbotActor actor, string id, string messageId, CancellationToken ct)
    {
        await RequireAsync(actor, id, ct);
        return Dto(await db.ChatbotMessages.AsNoTracking().SingleOrDefaultAsync(m => m.conversation_id == id && m.message_id == messageId, ct)
            ?? throw ChatbotException.Unavailable());
    }

    public async Task<ChatbotStart> StartAsync(ChatbotActor actor, string id, ChatbotSendRequest request, CancellationToken ct)
    {
        await RequireAsync(actor, id, ct);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ChatbotJson.Serialize(request))));
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is ChatbotMessageRecord or ChatbotAuditRecord).ToArray()) entry.State = EntityState.Detached;
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var replay = await db.ChatbotMessages.AsNoTracking().Where(m => m.conversation_id == id && m.request_id == request.ClientRequestId).ToListAsync(ct);
            if (replay.Count > 0)
            {
                if (replay.Any(m => m.request_hash != hash)) throw new ChatbotException(409, "request_reused", "Mã yêu cầu đã dùng cho nội dung khác.");
                var answer = replay.Single(m => m.sender == "ASSISTANT");
                if (answer.status == "GENERATING") throw new ChatbotException(409, "turn_active", "Câu trả lời đang được xử lý.", answer.message_id);
                return new ChatbotStart(new(Dto(replay.Single(m => m.sender == "USER")), Dto(answer)), true);
            }
            if (request.RetryOfMessageId is not null && !await db.ChatbotMessages.AnyAsync(m => m.conversation_id == id && m.message_id == request.RetryOfMessageId && m.sender == "ASSISTANT" && m.status != "GENERATING", ct))
                throw ChatbotException.Unavailable();
            var dayStart = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7)).Date.AddHours(-7);
            var spent = await db.ChatbotMessages.Where(m => m.actor_id == actor.UserId && m.created_at >= dayStart)
                .SumAsync(m => (long)m.input_tokens + m.output_tokens + m.reserved_tokens, ct);
            // A spoken turn is saved after it already happened; voice is metered by its own minute quota.
            var voice = request.Channel == "VOICE";
            var reservation = voice ? 0 : settings.MaxTurnTokenBudget;
            if (!voice && settings.EnforceDailyTokenBudget && spent + reservation > settings.DailyTokenBudget) throw new ChatbotException(429, "daily_budget", "Ngân sách trợ lý còn lại hôm nay chưa đủ cho một lượt mới. Vui lòng thử lại ngày mai.");
            var assistantId = Guid.NewGuid().ToString("N");
            var lease = Now.AddSeconds(settings.TurnTimeoutSeconds + 15);
            var acquired = await db.ChatbotConversations.Where(c => c.conversation_id == id && c.owner_user_id == actor.UserId && c.scope == actor.Scope && c.deleted_at == null && c.active_message_id == null)
                .ExecuteUpdateAsync(p => p.SetProperty(c => c.active_message_id, assistantId).SetProperty(c => c.lease_until, lease)
                    .SetProperty(c => c.ordinal, c => c.ordinal + 2).SetProperty(c => c.version, c => c.version + 1)
                    .SetProperty(c => c.updated_at, Now).SetProperty(c => c.actor_id, actor.UserId), ct);
            if (acquired == 0)
            {
                var active = await Owned(actor).Where(c => c.conversation_id == id).Select(c => c.active_message_id).SingleOrDefaultAsync(ct);
                throw new ChatbotException(409, "turn_active", "Hãy chờ câu trả lời hiện tại hoặc bấm Dừng.", active);
            }
            var conversation = await Owned(actor).SingleAsync(c => c.conversation_id == id, ct);
            var now = Local(Now);
            var user = new ChatbotMessage(Guid.NewGuid().ToString("N"), id, request.ClientRequestId, "USER", "COMPLETED", request.Content, false, [], [], [], now, now, conversation.ordinal - 1, 1,
                HasAttachments: request.AttachmentIds is { Length: > 0 }, ResponseStyle: request.ResponseStyle, Channel: request.Channel);
            var assistant = new ChatbotMessage(assistantId, id, request.ClientRequestId, "ASSISTANT", "GENERATING", "", true, [], [], [], now, null, conversation.ordinal, 1,
                HasAttachments: user.HasAttachments, ResponseStyle: request.ResponseStyle, Channel: request.Channel);
            ChatbotMessageRecord Row(ChatbotMessage m) => new() { message_id = m.Id, conversation_id = id, request_id = request.ClientRequestId,
                request_hash = hash, sender = m.Sender, status = m.Status, payload_json = ChatbotJson.Serialize(m), ordinal = m.Ordinal,
                version = 1, actor_id = actor.UserId, created_at = Now, updated_at = Now, reserved_tokens = m.Sender == "ASSISTANT" ? reservation : 0 };
            db.ChatbotMessages.AddRange(Row(user), Row(assistant));
            if (conversation.ordinal == 2)
                await db.ChatbotConversations.Where(c => c.conversation_id == id).ExecuteUpdateAsync(p => p.SetProperty(c => c.title, ChatbotPrivacy.Text(request.Content, 90)), ct);
            db.ChatbotAudits.Add(new() { message_id = assistantId, operation = "turn", outcome = "GENERATING", actor_id = actor.UserId, timestamp = Now });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new ChatbotStart(new(user, assistant), false);
        });
    }

    public Task<bool> IsGeneratingAsync(string id, string messageId, CancellationToken ct) => db.ChatbotConversations.AsNoTracking()
        .AnyAsync(c => c.conversation_id == id && c.deleted_at == null && c.active_message_id == messageId && c.lease_until > Now, ct);

    public async Task SaveAsync(ChatbotActor actor, ChatbotMessage message, string? provider, string? model, ChatbotUsage usage, CancellationToken ct)
    {
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var terminal = message.Status != "GENERATING";
        var payload = ChatbotJson.Serialize(message);
        var changed = await db.ChatbotMessages.Where(m => m.message_id == message.Id && m.actor_id == actor.UserId && m.status == "GENERATING" && m.version < message.Version)
            .ExecuteUpdateAsync(p => p.SetProperty(m => m.payload_json, payload).SetProperty(m => m.status, message.Status)
                .SetProperty(m => m.provider, provider).SetProperty(m => m.model_id, model)
                .SetProperty(m => m.input_tokens, m => message.Status == "CANCELLED" ? m.input_tokens + m.reserved_tokens : usage.InputTokens)
                .SetProperty(m => m.output_tokens, m => message.Status == "CANCELLED" ? m.output_tokens : usage.OutputTokens)
                .SetProperty(m => m.reserved_tokens, m => terminal ? 0 : m.reserved_tokens)
                .SetProperty(m => m.version, message.Version).SetProperty(m => m.updated_at, Now), ct);
        if (changed > 0 && terminal)
        {
            await db.ChatbotConversations.Where(c => c.conversation_id == message.ConversationId && c.active_message_id == message.Id)
                .ExecuteUpdateAsync(p => p.SetProperty(c => c.active_message_id, (string?)null).SetProperty(c => c.lease_until, (DateTime?)null).SetProperty(c => c.updated_at, Now), ct);
            await AuditToolAsync(actor, message.Id, "turn", message.Status, ct);
        }
        await tx.CommitAsync(ct);
        });
    }

    public async Task CancelAsync(ChatbotActor actor, string id, string messageId, CancellationToken ct)
    {
        var message = await MessageAsync(actor, id, messageId, ct);
        if (message.Sender != "ASSISTANT" || message.Status != "GENERATING") return;
        await SaveAsync(actor, message with { Status = "CANCELLED", CompletedAt = Local(Now), Version = message.Version + 1 }, null, null, new(0, 0), ct);
    }

    public async Task DeleteAsync(ChatbotActor actor, string id, CancellationToken ct)
    {
        await RequireAsync(actor, id, ct);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.ChatbotConversations.Where(c => c.conversation_id == id && c.owner_user_id == actor.UserId)
                .ExecuteUpdateAsync(p => p.SetProperty(c => c.deleted_at, Now).SetProperty(c => c.active_message_id, (string?)null)
                    .SetProperty(c => c.title, "Đã xóa").SetProperty(c => c.actor_id, actor.UserId).SetProperty(c => c.updated_at, Now), ct);
            // Remove content immediately, retaining only today's metering until retention cleanup.
            // Otherwise deleting a conversation would reset the caller's daily AI budget.
            await db.ChatbotMessages.Where(m => m.conversation_id == id).ExecuteUpdateAsync(p =>
                p.SetProperty(m => m.payload_json, "{}").SetProperty(m => m.feedback_reason, (string?)null)
                    .SetProperty(m => m.status, "DELETED").SetProperty(m => m.updated_at, Now)
                    .SetProperty(m => m.input_tokens, m => m.input_tokens + m.reserved_tokens)
                    .SetProperty(m => m.reserved_tokens, 0).SetProperty(m => m.version, m => m.version + 1), ct);
            await AuditToolAsync(actor, id, "conversation", "DELETED", ct);
            await tx.CommitAsync(ct);
        });
    }

    public async Task FeedbackAsync(ChatbotActor actor, string id, string messageId, bool helpful, string? reason, CancellationToken ct)
    {
        var message = await MessageAsync(actor, id, messageId, ct);
        if (message.Sender != "ASSISTANT" || message.Status == "GENERATING") throw new ChatbotException(400, "invalid_feedback", "Chỉ đánh giá câu trả lời đã kết thúc.");
        var safeReason = ChatbotPrivacy.Text(reason, 300);
        await db.ChatbotMessages.Where(m => m.message_id == messageId).ExecuteUpdateAsync(p => p.SetProperty(m => m.helpful, helpful).SetProperty(m => m.feedback_reason, safeReason).SetProperty(m => m.updated_at, Now), ct);
        await AuditToolAsync(actor, messageId, "feedback", helpful ? "HELPFUL" : "NOT_HELPFUL", ct);
    }

    public const string VoiceOperation = "voice_session";

    public async Task<int> VoiceSecondsTodayAsync(long userId, CancellationToken ct)
    {
        var dayStart = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7)).Date.AddHours(-7);
        var outcomes = await db.ChatbotAudits.AsNoTracking()
            .Where(a => a.actor_id == userId && a.operation == VoiceOperation && a.timestamp >= dayStart)
            .Select(a => a.outcome).ToListAsync(ct);
        return outcomes.Sum(o => int.TryParse(o.Split('s', 2)[0], out var seconds) ? seconds : 0);
    }

    public async Task RecordVoiceAsync(ChatbotActor actor, string sessionId, int seconds, string reason, CancellationToken ct)
    {
        var outcome = $"{Math.Max(0, seconds)}s:{reason}";
        db.ChatbotAudits.Add(new() { message_id = sessionId, operation = VoiceOperation, outcome = outcome[..Math.Min(40, outcome.Length)], actor_id = actor.UserId, timestamp = Now });
        await db.SaveChangesAsync(ct);
    }

    public async Task AuditToolAsync(ChatbotActor actor, string messageId, string tool, string outcome, CancellationToken ct)
    {
        db.ChatbotAudits.Add(new() { message_id = messageId, operation = tool, outcome = outcome, actor_id = actor.UserId, timestamp = Now });
        await db.SaveChangesAsync(ct);
    }
}
