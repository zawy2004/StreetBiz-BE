using System;
using System.Collections.Generic;

namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class ChatConversation
{
    public long conversation_id { get; set; }

    public long storefront_id { get; set; }

    public long customer_user_id { get; set; }

    public DateTime created_at { get; set; }

    public DateTime? last_message_at { get; set; }

    public virtual ICollection<ChatMessage> ChatMessages { get; set; } = new List<ChatMessage>();

    public virtual UserAccount customer_user { get; set; } = null!;

    public virtual Storefront storefront { get; set; } = null!;
}
