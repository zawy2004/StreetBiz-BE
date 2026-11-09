using StreetBiz.Application.Common.Models;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Tests.Support;
using static StreetBiz.Infrastructure.Tests.Support.SqliteTestDatabase;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>Buyer/seller chat: who can see a thread, paging, unread counts and read receipts.</summary>
public sealed class ChatRepositoryTests
{
    private const long Storefront = 1;
    private const long OtherCustomer = 21;
    private static readonly ChatParticipant Buyer = new(Customer, ChatParticipantSide.Customer);
    private static readonly ChatParticipant Seller = new(VendorA, ChatParticipantSide.Vendor);
    private static readonly ChatParticipant OtherSeller = new(VendorB, ChatParticipantSide.Vendor);
    private static readonly ChatParticipant OtherBuyer = new(OtherCustomer, ChatParticipantSide.Customer);

    private static async Task<(SqliteTestDatabase Db, TestClock Clock)> Arrange()
    {
        var db = await CreateAsync();
        db.Db.UserAccounts.Add(new UserAccount
        {
            user_id = OtherCustomer, phone_number = "0900000021", password_hash = "x", full_name = "User 21",
            role_code = "CUSTOMER", ward_unit_id = Ward, account_status = "ACTIVE",
        });
        db.Db.Storefronts.Add(new Storefront
        {
            storefront_id = Storefront, registration_id = RegistrationA, contract_id = ContractA,
            storefront_name = "Bánh mì A", availability_status = "OPEN",
        });
        await db.Db.SaveChangesAsync();
        return (db, new TestClock(new DateTime(2026, 11, 1, 3, 0, 0)));
    }

    [Fact]
    public async Task Starting_a_conversation_twice_returns_the_same_thread()
    {
        var (db, clock) = await Arrange();
        using var _ = db;
        var repository = new ChatRepository(db.Db, clock);

        var first = await repository.StartConversationAsync(Customer, Storefront, default);
        var again = await repository.StartConversationAsync(Customer, Storefront, default);

        Assert.NotNull(first);
        Assert.Equal(first!.ConversationId, again!.ConversationId);
        Assert.Equal("Bánh mì A", first.StorefrontName);
        Assert.Equal("User 20", first.CustomerName);
        Assert.Equal(DateTimeKind.Utc, again.CreatedAt.Kind);
        Assert.Null(await repository.StartConversationAsync(Customer, 999, default));
    }

    [Fact]
    public async Task Only_the_buyer_and_the_storefronts_vendor_see_the_thread()
    {
        var (db, clock) = await Arrange();
        using var _ = db;
        var repository = new ChatRepository(db.Db, clock);
        var id = (await repository.StartConversationAsync(Customer, Storefront, default))!.ConversationId;

        Assert.NotNull(await repository.GetConversationAsync(Buyer, id, default));
        Assert.NotNull(await repository.GetConversationAsync(Seller, id, default));
        Assert.Null(await repository.GetConversationAsync(OtherSeller, id, default));
        Assert.Null(await repository.GetConversationAsync(OtherBuyer, id, default));
        Assert.Single(await repository.ListConversationsAsync(Seller, default));
        Assert.Empty(await repository.ListConversationsAsync(OtherSeller, default));
        Assert.Null(await repository.SendMessageAsync(OtherBuyer, id, "xin chào", default));
        Assert.Empty((await repository.ListMessagesAsync(OtherSeller, id, 20, null, default)).Messages);
        Assert.Equal(0, await repository.MarkReadAsync(OtherBuyer, id, default));

        var users = await repository.GetThreadUsersAsync(id, default);
        Assert.Equal(new ChatThreadUsers(Customer, VendorA), users);
        Assert.Null(await repository.GetThreadUsersAsync(999, default));
    }

    [Fact]
    public async Task Sending_updates_the_thread_summary_and_the_other_sides_unread_count()
    {
        var (db, clock) = await Arrange();
        using var _ = db;
        var repository = new ChatRepository(db.Db, clock);
        var id = (await repository.StartConversationAsync(Customer, Storefront, default))!.ConversationId;

        var sent = await repository.SendMessageAsync(Buyer, id, "Còn bánh mì không?", default);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await repository.SendMessageAsync(Buyer, id, "Cho 2 ổ nhé", default);

        Assert.Equal("User 20", sent!.SenderName);
        Assert.Null(sent.ReadAt);
        var sellerView = (await repository.GetConversationAsync(Seller, id, default))!;
        Assert.Equal("Cho 2 ổ nhé", sellerView.LastMessageBody);
        Assert.Equal(Customer, sellerView.LastMessageSenderUserId);
        Assert.Equal(2, sellerView.UnreadCount);
        Assert.Equal(0, (await repository.GetConversationAsync(Buyer, id, default))!.UnreadCount);
        Assert.Equal(2, await repository.CountUnreadAsync(Seller, default));
        Assert.Equal(0, await repository.CountUnreadAsync(Buyer, default));
    }

    [Fact]
    public async Task Marking_read_clears_only_the_other_sides_messages()
    {
        var (db, clock) = await Arrange();
        using var _ = db;
        var repository = new ChatRepository(db.Db, clock);
        var id = (await repository.StartConversationAsync(Customer, Storefront, default))!.ConversationId;
        await repository.SendMessageAsync(Buyer, id, "a", default);
        await repository.SendMessageAsync(Seller, id, "b", default);

        Assert.Equal(1, await repository.MarkReadAsync(Seller, id, default));
        Assert.Equal(0, await repository.MarkReadAsync(Seller, id, default));

        var reader = new ChatRepository(db.NewContext(), clock);
        Assert.Equal(0, await reader.CountUnreadAsync(Seller, default));
        Assert.Equal(1, await reader.CountUnreadAsync(Buyer, default));
    }

    [Fact]
    public async Task Messages_page_backwards_and_come_back_oldest_first()
    {
        var (db, clock) = await Arrange();
        using var _ = db;
        var repository = new ChatRepository(db.Db, clock);
        var id = (await repository.StartConversationAsync(Customer, Storefront, default))!.ConversationId;
        for (var i = 1; i <= 5; i++)
        {
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            await repository.SendMessageAsync(i % 2 == 0 ? Seller : Buyer, id, $"m{i}", default);
        }

        var latest = await repository.ListMessagesAsync(Buyer, id, 2, null, default);
        Assert.Equal(new[] { "m4", "m5" }, latest.Messages.Select(m => m.Body));
        Assert.True(latest.HasMore);
        Assert.All(latest.Messages, m => Assert.Equal(DateTimeKind.Utc, m.SentAt.Kind));

        var older = await repository.ListMessagesAsync(Buyer, id, 2, latest.Messages[0].MessageId, default);
        Assert.Equal(new[] { "m2", "m3" }, older.Messages.Select(m => m.Body));
        Assert.True(older.HasMore);

        var oldest = await repository.ListMessagesAsync(Buyer, id, 2, older.Messages[0].MessageId, default);
        Assert.Equal(new[] { "m1" }, oldest.Messages.Select(m => m.Body));
        Assert.False(oldest.HasMore);
    }

    [Fact]
    public async Task The_most_recently_active_thread_is_listed_first()
    {
        var (db, clock) = await Arrange();
        using var _ = db;
        db.Db.RentalApplications.Add(new RentalApplication { application_id = 2, registration_id = RegistrationB, slot_id = SlotFree, application_method = "MANUAL_SELECTED", requested_term_days = 30, application_status = "APPROVED" });
        db.Db.RentalContracts.Add(new RentalContract { contract_id = 2, application_id = 2, slot_id = SlotFree, vendor_id = VendorB, start_date = new DateOnly(2026, 9, 1), end_date = new DateOnly(2026, 12, 1), contract_status = "ACTIVE" });
        db.Db.Storefronts.Add(new Storefront { storefront_id = 2, registration_id = RegistrationB, contract_id = 2, storefront_name = "Xe B", availability_status = "OPEN" });
        await db.Db.SaveChangesAsync();
        var repository = new ChatRepository(db.Db, clock);
        var a = (await repository.StartConversationAsync(Customer, Storefront, default))!.ConversationId;
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var b = (await repository.StartConversationAsync(Customer, 2, default))!.ConversationId;
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await repository.SendMessageAsync(Buyer, a, "lên đầu", default);

        var order = (await repository.ListConversationsAsync(Buyer, default)).Select(c => c.ConversationId).ToList();

        Assert.Equal(new[] { a, b }, order);
    }
}
