namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public sealed class ChatbotConversationRecord
{
    public string conversation_id { get; set; } = "";
    public long owner_user_id { get; set; }
    public string create_request_id { get; set; } = "";
    public string scope { get; set; } = "";
    public string title { get; set; } = "Cuộc trò chuyện mới";
    public DateTime created_at { get; set; }
    public DateTime updated_at { get; set; }
    public DateTime? deleted_at { get; set; }
    public string? active_message_id { get; set; }
    public DateTime? lease_until { get; set; }
    public long ordinal { get; set; }
    public long version { get; set; }
    public long actor_id { get; set; }
}

public sealed class ChatbotMessageRecord
{
    public string message_id { get; set; } = "";
    public string conversation_id { get; set; } = "";
    public string request_id { get; set; } = "";
    public string request_hash { get; set; } = "";
    public string sender { get; set; } = "";
    public string status { get; set; } = "";
    public string payload_json { get; set; } = "";
    public string? provider { get; set; }
    public string? model_id { get; set; }
    public int input_tokens { get; set; }
    public int output_tokens { get; set; }
    public int reserved_tokens { get; set; }
    public long ordinal { get; set; }
    public long version { get; set; }
    public DateTime created_at { get; set; }
    public DateTime updated_at { get; set; }
    public long actor_id { get; set; }
    public bool? helpful { get; set; }
    public string? feedback_reason { get; set; }
}

public sealed class ChatbotAuditRecord
{
    public long audit_id { get; set; }
    public string message_id { get; set; } = "";
    public string operation { get; set; } = "";
    public string outcome { get; set; } = "";
    public long actor_id { get; set; }
    public DateTime timestamp { get; set; }
}
