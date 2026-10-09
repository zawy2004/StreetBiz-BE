using System.Text.Json;

namespace StreetBiz.Application.Features.Chatbot;

public static class ChatbotJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)!;
}

public sealed class ChatbotSettings
{
    public bool Enabled { get; set; } = true;
    public bool GuestEnabled { get; set; } = true;
    public bool AttachmentsEnabled { get; set; }
    public bool CrossProviderFallback { get; set; } = true;
    public int MaxQuestionCharacters { get; set; } = 4000;
    public int MaxOutputTokens { get; set; } = 2000;
    public int MaxContextCharacters { get; set; } = 18000;
    public int MaxToolCalls { get; set; } = 6;
    public int MaxModelSteps { get; set; } = 5;
    public int TurnTimeoutSeconds { get; set; } = 75;
    public int ProviderTimeoutSeconds { get; set; } = 25;
    public int ImageProviderTimeoutSeconds { get; set; } = 25;
    public int DailyTokenBudget { get; set; } = 100000;
    public bool EnforceDailyTokenBudget { get; set; } = true;
    public bool IndependentKeyQuotas { get; set; }
    public int MaxTurnTokenBudget { get; set; } = 28000;
    public int GlobalConcurrentTurns { get; set; } = 8;
    public int RetentionDays { get; set; } = 30;
}

public sealed class ChatbotProviderException(string category, bool transient) : Exception(category)
{
    public string Category { get; } = category;
    public bool Transient { get; } = transient;
}

public sealed record ChatbotActor(long UserId, long SessionId, string Role, int? WardId, long? VendorId)
{
    public string Scope => $"{Role}:{WardId}:{VendorId}";
    public static readonly ChatbotActor Guest = new(0, 0, "GUEST", null, null);
}

public sealed class ChatbotException(int status, string code, string message, string? activeMessageId = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? ActiveMessageId { get; } = activeMessageId;
    public static ChatbotException Unavailable() => new(404, "not_found", "Không tìm thấy hội thoại hoặc bạn không có quyền truy cập.");
}

public sealed record ChatbotPageContext(string PageKey, string? EntityId = null);
/// <summary>Device position the user chose to share for this request only; never persisted with history.</summary>
public sealed record ChatbotLocation(decimal Latitude, decimal Longitude);
public sealed record ChatbotSendRequest(string ClientRequestId, string Content,
    ChatbotPageContext? PageContext = null, string? RetryOfMessageId = null, string[]? AttachmentIds = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ResponseStyle = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ChatbotLocation? Location = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Channel = null);
public sealed record ChatbotGuestRequest(string Content, ChatbotHistoryEntry[]? History = null, string? ResponseStyle = null);
public sealed record ChatbotHistoryEntry(string Role, string Content);
public sealed record ChatbotSource(string Id, string Title, string Kind, DateTimeOffset? ObservedAt,
    string? DocumentVersion, string? ActionId);
public sealed record ChatbotAction(string Id, string Label, string Kind, string Route);
public sealed record ChatbotField(string Label, string Value);
public sealed record ChatbotCard(string Kind, string Title, IReadOnlyList<ChatbotField> Fields, string? ActionId = null, string? ImageUrl = null,
    ChatbotPlace? Place = null);
/// <summary>Public listing facts for map/sort UI. Distance exists only when the user shared a position.</summary>
public sealed record ChatbotPlace(decimal Latitude, decimal Longitude, double? DistanceMeters, bool IsOpenNow,
    decimal? Rating, int RatingCount, decimal? PriceVnd);
public sealed record ChatbotBriefingItem(string Tone, string Title, string Detail, ChatbotAction? Action);
public sealed record ChatbotBriefing(DateTimeOffset ObservedAt, IReadOnlyList<ChatbotBriefingItem> Items);
public sealed record ChatbotSlotPermit(string SlotCode, string? ZoneName, string? VendorName, long? ContractId,
    string? ContractStatus, string EffectiveStatus, DateOnly? EndDate);
public sealed record ChatbotError(string Code, string Message, bool Retryable);
public sealed record ChatbotChecklistItem(string Text, string? ActionId = null);
public sealed record ChatbotMessage(string Id, string ConversationId, string ClientRequestId, string Sender,
    string Status, string Content, bool IsAiGenerated, IReadOnlyList<ChatbotSource> Sources,
    IReadOnlyList<ChatbotCard> Cards, IReadOnlyList<ChatbotAction> Actions, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, long Ordinal, long Version, ChatbotError? Error = null,
    bool HasAttachments = false, IReadOnlyList<ChatbotChecklistItem>? Checklist = null, string? ResponseStyle = null,
    string? Channel = null);
public sealed record ChatbotConversation(string Id, string Title, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, string? ActiveMessageId);
public sealed record ChatbotPage<T>(IReadOnlyList<T> Items, string? NextCursor);
public sealed record ChatbotSendResult(ChatbotMessage UserMessage, ChatbotMessage AssistantMessage);
public sealed record ChatbotStart(ChatbotSendResult Messages, bool IsReplay);
public sealed record ChatbotEvent(string EventId, string ConversationId, string MessageId,
    string ClientRequestId, int Attempt, long Sequence, long Version, DateTimeOffset OccurredAt,
    string Type, object Payload);
public sealed record ChatbotUsage(int InputTokens, int OutputTokens);
public sealed record ChatbotToolDefinition(string Name, string Description, JsonElement Parameters);
public sealed record ChatbotToolCall(string Id, string Name, string Arguments);
public sealed record ChatbotEvidence(string Tool, string Json, ChatbotSource Source,
    IReadOnlyList<ChatbotCard> Cards, IReadOnlyList<ChatbotAction> Actions, bool Authoritative,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<ChatbotPublicImage>? PublicImages = null);
public sealed record ChatbotProviderRequest(string SystemPrompt, IReadOnlyList<ChatbotHistoryEntry> History,
    string Question, IReadOnlyList<ChatbotToolDefinition> Tools, IReadOnlyList<ChatbotEvidence> Evidence,
    IReadOnlyList<ChatbotToolExchange> Exchanges, bool FinalAnswer, bool Complex = false,
    IReadOnlyList<ChatbotImage>? Images = null);
public sealed record ChatbotImage(byte[] Bytes, string MimeType, string? Label = null);
public sealed record ChatbotPublicImage(string Url, string Label);
public interface IChatbotPublicImages
{
    Task<IReadOnlyList<ChatbotImage>> ReadAsync(IReadOnlyList<ChatbotPublicImage> candidates, CancellationToken ct);
}
public sealed record ChatbotUploadRequest(string PngBase64, bool Consent);
public sealed record ChatbotAttachment(string Id, DateTimeOffset ExpiresAt);
public sealed record ChatbotListItem(long Id, string Label, string? Status, decimal? Amount, DateOnly? Date);
public interface IChatbotListReader
{
    Task<IReadOnlyList<ChatbotListItem>> ReadAsync(ChatbotActor actor, string tool, CancellationToken ct);
    /// <summary>Live permit validity for a slot inside the officer's own ward; null when not in scope.</summary>
    Task<ChatbotSlotPermit?> SlotPermitAsync(ChatbotActor actor, string slotCode, CancellationToken ct);
}
// Native assistant blocks are preserved only inside a provider's current tool loop.
public sealed record ChatbotToolExchange(string Provider, JsonElement Assistant, IReadOnlyList<ChatbotToolReply> Replies);
public sealed record ChatbotToolReply(string Id, string Name, string Json);
public sealed record ChatbotProviderResult(string Provider, string Model, string Text,
    IReadOnlyList<ChatbotToolCall> ToolCalls, JsonElement? NativeAssistant, ChatbotUsage Usage);

public interface IChatbotActorResolver
{
    Task<ChatbotActor> RequireAsync(CancellationToken ct);
    Task<bool> IsActiveAsync(ChatbotActor actor, CancellationToken ct);
}

public interface IChatbotStore
{
    Task<ChatbotConversation> CreateAsync(ChatbotActor actor, string requestId, CancellationToken ct);
    Task<ChatbotPage<ChatbotConversation>> ListAsync(ChatbotActor actor, string? before, CancellationToken ct);
    Task<ChatbotConversation> RequireAsync(ChatbotActor actor, string id, CancellationToken ct);
    Task<ChatbotPage<ChatbotMessage>> HistoryAsync(ChatbotActor actor, string id, long? before, CancellationToken ct);
    Task<ChatbotMessage> MessageAsync(ChatbotActor actor, string id, string messageId, CancellationToken ct);
    Task<ChatbotStart> StartAsync(ChatbotActor actor, string id, ChatbotSendRequest request, CancellationToken ct);
    Task<bool> IsGeneratingAsync(string id, string messageId, CancellationToken ct);
    Task SaveAsync(ChatbotActor actor, ChatbotMessage message, string? provider, string? model,
        ChatbotUsage usage, CancellationToken ct);
    Task CancelAsync(ChatbotActor actor, string id, string messageId, CancellationToken ct);
    Task DeleteAsync(ChatbotActor actor, string id, CancellationToken ct);
    Task FeedbackAsync(ChatbotActor actor, string id, string messageId, bool helpful, string? reason, CancellationToken ct);
    Task AuditToolAsync(ChatbotActor actor, string messageId, string tool, string outcome, CancellationToken ct);
    /// <summary>Spoken seconds already used today (Asia/Ho_Chi_Minh), shared by every API instance.</summary>
    Task<int> VoiceSecondsTodayAsync(long userId, CancellationToken ct);
    Task RecordVoiceAsync(ChatbotActor actor, string sessionId, int seconds, string reason, CancellationToken ct);
}

public interface IChatbotModel
{
    string Name { get; }
    bool Available { get; }
    Task<ChatbotProviderResult> GenerateAsync(ChatbotProviderRequest request,
        Func<string, CancellationToken, Task> onDelta, CancellationToken ct);
}

public interface IChatbotEvents
{
    Task PublishAsync(ChatbotActor actor, ChatbotEvent message, CancellationToken ct);
}

public interface IChatbotAttachments
{
    ChatbotAttachment Upload(ChatbotActor actor, ChatbotUploadRequest request);
    void Remove(ChatbotActor actor, string id);
    Task<IReadOnlyList<ChatbotImage>> ReadAsync(ChatbotActor actor, string[] ids, CancellationToken ct);
}
