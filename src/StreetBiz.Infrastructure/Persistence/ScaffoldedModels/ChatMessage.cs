using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class ChatMessage
{
    public long message_id { get; set; }

    public long conversation_id { get; set; }

    public long sender_user_id { get; set; }

    public string body { get; set; } = null!;

    public DateTime sent_at { get; set; }

    public DateTime? read_at { get; set; }

    public virtual ChatConversation conversation { get; set; } = null!;

    public virtual UserAccount sender_user { get; set; } = null!;
}
