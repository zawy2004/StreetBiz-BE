using Microsoft.EntityFrameworkCore;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence;

public partial class StreetBizDbContext
{
    public DbSet<ChatbotConversationRecord> ChatbotConversations => Set<ChatbotConversationRecord>();
    public DbSet<ChatbotMessageRecord> ChatbotMessages => Set<ChatbotMessageRecord>();
    public DbSet<ChatbotAuditRecord> ChatbotAudits => Set<ChatbotAuditRecord>();

    private static void ConfigureChatbot(ModelBuilder builder)
    {
        var conversation = builder.Entity<ChatbotConversationRecord>();
        conversation.ToTable("ChatbotConversations");
        conversation.HasKey(x => x.conversation_id);
        conversation.Property(x => x.conversation_id).HasMaxLength(32);
        conversation.Property(x => x.create_request_id).HasMaxLength(36);
        conversation.Property(x => x.scope).HasMaxLength(100);
        conversation.Property(x => x.title).HasMaxLength(200);
        conversation.Property(x => x.active_message_id).HasMaxLength(32);
        conversation.HasIndex(x => new { x.owner_user_id, x.create_request_id }).IsUnique();
        conversation.HasIndex(x => new { x.owner_user_id, x.updated_at });
        conversation.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.owner_user_id).OnDelete(DeleteBehavior.Restrict);
        var message = builder.Entity<ChatbotMessageRecord>();
        message.ToTable("ChatbotMessages", t =>
        {
            t.HasCheckConstraint("CK_ChatbotMessages_Sender", "sender IN ('USER','ASSISTANT')");
            t.HasCheckConstraint("CK_ChatbotMessages_Status", "status IN ('GENERATING','COMPLETED','FAILED','CANCELLED','INTERRUPTED','DELETED')");
        });
        message.HasKey(x => x.message_id);
        message.Property(x => x.message_id).HasMaxLength(32);
        message.Property(x => x.conversation_id).HasMaxLength(32);
        message.Property(x => x.request_id).HasMaxLength(36);
        message.Property(x => x.request_hash).HasMaxLength(64);
        message.Property(x => x.sender).HasMaxLength(12);
        message.Property(x => x.status).HasMaxLength(16);
        message.Property(x => x.provider).HasMaxLength(30);
        message.Property(x => x.model_id).HasMaxLength(150);
        message.Property(x => x.feedback_reason).HasMaxLength(300);
        message.HasIndex(x => new { x.conversation_id, x.request_id, x.sender }).IsUnique();
        message.HasIndex(x => new { x.conversation_id, x.ordinal }).IsUnique();
        message.HasIndex(x => new { x.actor_id, x.created_at });
        message.HasOne<ChatbotConversationRecord>().WithMany().HasForeignKey(x => x.conversation_id).OnDelete(DeleteBehavior.Cascade);
        var audit = builder.Entity<ChatbotAuditRecord>();
        audit.ToTable("ChatbotAudits");
        audit.HasKey(x => x.audit_id);
        audit.Property(x => x.message_id).HasMaxLength(32);
        audit.Property(x => x.operation).HasMaxLength(80);
        audit.Property(x => x.outcome).HasMaxLength(40);
        audit.HasIndex(x => new { x.message_id, x.timestamp });
    }
}
