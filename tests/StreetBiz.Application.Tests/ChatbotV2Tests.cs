using MediatR;
using Moq;
using StreetBiz.Application.DTOs.Commerce;
using StreetBiz.Application.DTOs.Finance;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Application.Features.Finance.GetSummary;
using Fixture = StreetBiz.Application.Tests.ChatbotServiceTests.Fixture;
using FoodModel = StreetBiz.Application.Tests.ChatbotServiceTests.FoodModel;

namespace StreetBiz.Application.Tests;

public sealed class ChatbotV2Tests
{
    private static StorefrontSummaryDto Store(long id, bool open, double? distance) => new(id, $"Quầy {id}", null, null, id, $"Đường {id}", 1, "Hải Châu", "Khu A",
        $"HC-0{id}", 16.06m + id / 1000m, 108.22m, distance, open, [], 4.5m, 12, 3, 30000, ["Bún"]);
    private static MarketplaceMenuItemDto Dish(long store, string name) => new(store * 10, store, $"Quầy {store}", name, null, null, 35000, "AVAILABLE", 1, "Bún");

    [Fact]
    public async Task Photo_alternatives_are_searched_and_reported_as_unverified_candidates()
    {
        using var f = new Fixture(); f.Settings.AttachmentsEnabled = true; f.Images = [new([1, 2], "image/png")];
        f.Sender.Setup(s => s.Send(It.IsAny<SearchMarketplaceMenuQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SearchMarketplaceMenuQuery q, CancellationToken _) => q.Query == "bún bò" ? [Dish(2, "Bún bò Huế")] : Array.Empty<MarketplaceMenuItemDto>());
        f.Sender.Setup(s => s.Send(It.IsAny<ListStorefrontsQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync([Store(2, true, null)]);
        var model = new FoodModel(arguments: new { query = "bún riêu", alternatives = new[] { "bún bò", "Bún riêu" }, confidence = "medium", cues = "Nước dùng đỏ, có rau sống" });
        var result = await f.Service(model).SendAsync("conversation", f.Request with { Content = "Món này là gì?", AttachmentIds = [Guid.NewGuid().ToString()] }, default);

        Assert.Equal("COMPLETED", result.AssistantMessage.Status);
        var tools = Assert.Single(model.First!.Tools);
        Assert.Contains("alternatives", tools.Parameters.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        var candidates = Assert.Single(result.AssistantMessage.Cards, c => c.Kind == ChatbotFoodPhoto.CandidatesKind);
        // The duplicate of the main name is dropped; the alternative is weaker than the first choice.
        Assert.Equal(["bún riêu", "bún bò", ChatbotFoodPhoto.CuesLabel], candidates.Fields.Select(x => x.Label));
        Assert.Equal(["Có thể", "Chưa chắc"], candidates.Fields.Take(2).Select(x => x.Value));
        Assert.Contains(result.AssistantMessage.Cards, c => c.Title == "Quầy 2" && c.Place is { IsOpenNow: true });
        Assert.Contains("Cũng có thể là **bún bò**", result.AssistantMessage.Content);
        Assert.Contains(result.AssistantMessage.Sources, s => s.Kind == "IMAGE_ANALYSIS");
        f.Sender.Verify(s => s.Send(It.Is<SearchMarketplaceMenuQuery>(q => q.Query == "bún bò"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Photo_hints_with_invalid_shape_are_rejected_before_any_search()
    {
        using var f = new Fixture(); f.Settings.AttachmentsEnabled = true; f.Images = [new([1], "image/png")];
        var model = new FoodModel(arguments: new { query = "phở", confidence = "certain" });
        var result = await f.Service(model).SendAsync("conversation", f.Request with { AttachmentIds = [Guid.NewGuid().ToString()] }, default);
        Assert.Equal("FAILED", result.AssistantMessage.Status);
        f.Sender.Verify(s => s.Send(It.IsAny<SearchMarketplaceMenuQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Shared_position_is_coarsened_ranks_stalls_and_never_enters_the_replay_hash()
    {
        using var f = new Fixture();
        f.Sender.Setup(s => s.Send(It.IsAny<SearchMarketplaceMenuQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Dish(1, "Bún chả"), Dish(2, "Bún chả"), Dish(3, "Bún thịt nướng")]);
        f.Sender.Setup(s => s.Send(It.IsAny<ListStorefrontsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Store(1, false, 200), Store(2, true, 900), Store(3, true, 50)]);
        var result = await f.Service().SendAsync("conversation", f.Request with
        {
            Content = "Tôi muốn ăn bún chả", Location = new(16.067891m, 108.223456m)
        }, default);

        f.Sender.Verify(s => s.Send(It.Is<ListStorefrontsQuery>(q => q.Latitude == 16.068m && q.Longitude == 108.223m), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        f.Store.Verify(s => s.StartAsync(It.IsAny<ChatbotActor>(), "conversation", It.Is<ChatbotSendRequest>(r => r.Location == null), It.IsAny<CancellationToken>()));
        // Exact dish name first, then open stalls, then distance.
        Assert.Equal(["Quầy 2", "Quầy 1", "Quầy 3"], result.AssistantMessage.Cards.Select(c => c.Title));
        Assert.Contains(result.AssistantMessage.Cards[0].Fields, x => x.Label == "Khoảng cách" && x.Value == "~900 m");
        Assert.Contains("đang trong giờ mở cửa", result.AssistantMessage.Content);
    }

    [Fact]
    public async Task Without_position_no_distance_is_claimed()
    {
        using var f = new Fixture();
        f.Sender.Setup(s => s.Send(It.IsAny<SearchMarketplaceMenuQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync([Dish(1, "Bún chả")]);
        f.Sender.Setup(s => s.Send(It.IsAny<ListStorefrontsQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync([Store(1, true, null)]);
        var result = await f.Service().SendAsync("conversation", f.Request with { Content = "Tôi muốn ăn bún chả" }, default);
        var card = Assert.Single(result.AssistantMessage.Cards);
        Assert.DoesNotContain(card.Fields, x => x.Label == "Khoảng cách");
        Assert.Null(card.Place!.DistanceMeters);
        Assert.DoesNotContain(card.Fields, x => x.Value.Contains('\u0000'));
    }

    [Theory]
    [InlineData(91, 108)]
    [InlineData(16, 181)]
    public void Out_of_range_position_is_rejected(double lat, double lng) =>
        Assert.Equal("invalid_location", Assert.Throws<ChatbotException>(() => ChatbotPrivacy.Validate(
            new(Guid.NewGuid().ToString(), "Tìm quán", Location: new((decimal)lat, (decimal)lng)), new())).Code);

    [Theory]
    [InlineData("WARD_AUTHORITY", "Ô HC-08 còn hiệu lực không?", "HC-08")]
    [InlineData("WARD_AUTHORITY", "kiểm tra giấy phép ô hc12", "HC12")]
    [InlineData("WARD_AUTHORITY", "Tóm tắt tình hình phường", null)]
    [InlineData("VENDOR", "Ô HC-08 còn hiệu lực không?", null)]
    public void Slot_permit_intent_is_officer_only(string role, string question, string? expected) =>
        Assert.Equal(expected, ChatbotIntent.SlotPermit(role, question));

    [Fact]
    public async Task Officer_slot_permit_lookup_is_live_and_scoped_by_the_reader()
    {
        using var f = new Fixture();
        f.Actors.Setup(a => a.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ChatbotActor(5, 6, "WARD_AUTHORITY", 3, null));
        f.Lists.Setup(l => l.SlotPermitAsync(It.IsAny<ChatbotActor>(), "HC-08", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatbotSlotPermit("HC-08", "Khu A", "Bún chả Phố Nhỏ", 9, "ACTIVE", "SUSPENDED", new DateOnly(2026, 12, 31)));
        var result = await f.Service().SendAsync("conversation", f.Request with { Content = "Ô HC-08 còn hiệu lực không?" }, default);
        var card = Assert.Single(result.AssistantMessage.Cards);
        Assert.Contains(card.Fields, x => x.Label == "Hiệu lực lúc tra cứu" && x.Value == "Tạm đình chỉ");
        Assert.Contains(result.AssistantMessage.Sources, s => s.Kind == "LIVE_DATA" && s.Id.StartsWith("live:ward.slot_permit:"));
        f.Lists.Verify(l => l.SlotPermitAsync(It.Is<ChatbotActor>(a => a.WardId == 3), "HC-08", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("{\"slotCode\":\"HC 08; DROP\"}")]
    [InlineData("{\"slotCode\":\"HC-08\",\"wardId\":9}")]
    [InlineData("{}")]
    public async Task Slot_permit_arguments_are_strict(string args)
    {
        var actors = new Mock<IChatbotActorResolver>(); actors.Setup(a => a.IsActiveAsync(It.IsAny<ChatbotActor>(), default)).ReturnsAsync(true);
        var lists = new Mock<IChatbotListReader>(MockBehavior.Strict);
        var tools = new ChatbotTools(Mock.Of<ISender>(), actors.Object, new(), TimeProvider.System, lists.Object);
        Assert.Equal(400, (await Assert.ThrowsAsync<ChatbotException>(() => tools.ExecuteAsync(new(1, 1, "WARD_AUTHORITY", 3, null), "ward.slot_permit", args, "", default))).Status);
        lists.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("CUSTOMER")]
    [InlineData("VENDOR")]
    [InlineData("PLATFORM_ADMIN")]
    public async Task Slot_permit_tool_is_denied_outside_ward_authority(string role)
    {
        var tools = new ChatbotTools(Mock.Of<ISender>(), Mock.Of<IChatbotActorResolver>(), new(), TimeProvider.System, Mock.Of<IChatbotListReader>(MockBehavior.Strict));
        Assert.Equal(403, (await Assert.ThrowsAsync<ChatbotException>(() => tools.ExecuteAsync(new(1, 1, role, 3, 2), "ward.slot_permit", "{\"slotCode\":\"HC-08\"}", "", default))).Status);
    }

    [Fact]
    public async Task Vendor_briefing_lists_due_money_expiring_contracts_and_revisions_only()
    {
        var actors = new Mock<IChatbotActorResolver>(); actors.Setup(a => a.IsActiveAsync(It.IsAny<ChatbotActor>(), default)).ReturnsAsync(true);
        var sender = new Mock<ISender>();
        sender.Setup(s => s.Send(It.IsAny<GetFinanceSummaryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinanceSummaryDto(300000, 200000, 500000, 1, new DateOnly(2026, 10, 15)));
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime);
        var lists = new Mock<IChatbotListReader>();
        lists.Setup(l => l.ReadAsync(It.IsAny<ChatbotActor>(), "vendor.contracts", It.IsAny<CancellationToken>())).ReturnsAsync([
            new ChatbotListItem(11, "HC-08", "ACTIVE", null, today.AddDays(10)),
            new ChatbotListItem(12, "HC-09", "ACTIVE", null, today.AddDays(90)),
            new ChatbotListItem(13, "HC-10", "EXPIRED", null, today.AddDays(5))]);
        lists.Setup(l => l.ReadAsync(It.IsAny<ChatbotActor>(), "vendor.registrations", It.IsAny<CancellationToken>())).ReturnsAsync([
            new ChatbotListItem(21, "FIXED", "MORE_INFORMATION_REQUIRED", null, null),
            new ChatbotListItem(22, "FIXED", "APPROVED", null, null)]);
        var tools = new ChatbotTools(sender.Object, actors.Object, new(), TimeProvider.System, lists.Object);

        var briefing = await tools.BriefingAsync(new(1, 2, "VENDOR", null, 4), default);

        Assert.Equal(3, briefing.Items.Count);
        Assert.Contains(briefing.Items, i => i.Tone == "warning" && i.Detail.Contains("500.000 ₫") && i.Action!.Route == "/vendor/finance");
        Assert.Contains(briefing.Items, i => i.Action!.Route == "/vendor/slots/contracts/11");
        Assert.Contains(briefing.Items, i => i.Action!.Route == "/vendor/registrations/21");
    }

    [Fact]
    public async Task Briefing_requires_an_active_session()
    {
        var actors = new Mock<IChatbotActorResolver>(); actors.Setup(a => a.IsActiveAsync(It.IsAny<ChatbotActor>(), default)).ReturnsAsync(false);
        var tools = new ChatbotTools(Mock.Of<ISender>(MockBehavior.Strict), actors.Object, new(), TimeProvider.System, Mock.Of<IChatbotListReader>(MockBehavior.Strict));
        Assert.Equal(401, (await Assert.ThrowsAsync<ChatbotException>(() => tools.BriefingAsync(new(1, 2, "VENDOR", null, 4), default))).Status);
    }

    [Fact]
    public async Task Live_answers_read_like_a_person_but_copy_every_number_from_the_cards()
    {
        using var f = new Fixture();
        f.Actors.Setup(a => a.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ChatbotActor(1, 2, "VENDOR", null, 4));
        f.Sender.Setup(s => s.Send(It.IsAny<GetFinanceSummaryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinanceSummaryDto(300000, 200000, 500000, 1, new DateOnly(2026, 10, 15)));
        var result = await f.Service().SendAsync("conversation", f.Request with { Content = "Tôi cần đóng khoản phí nào?" }, default);
        Assert.StartsWith("Bạn đang cần thanh toán **500.000 VND** (phí 300.000 VND, phạt 200.000 VND). Có **1 khoản quá hạn**. Hạn gần nhất: **15/10/2026**.",
            result.AssistantMessage.Content);
    }

    [Fact]
    public void Summaries_cover_ward_permit_and_contract_lists()
    {
        ChatbotEvidence Live(string tool, params ChatbotCard[] cards) =>
            new(tool, "{}", new("live", "t", "LIVE_DATA", DateTimeOffset.UtcNow, null, null), cards, [], true);
        ChatbotCard Card(string title, params (string, string)[] fields) => new("status", title, fields.Select(x => new ChatbotField(x.Item1, x.Item2)).ToArray());
        Assert.Equal("Ô HC-08 đang **Tạm đình chỉ** lúc tra cứu, người bán: Bún chả Phố Nhỏ, hợp đồng đến ngày 31/12/2026.",
            ChatbotSummaries.Lead([Live("ward.slot_permit", Card("Ô HC-08", ("Hiệu lực lúc tra cứu", "Tạm đình chỉ"), ("Người bán", "Bún chả Phố Nhỏ"), ("Đến ngày", "31/12/2026")))]));
        Assert.Equal("Bạn có **2 hợp đồng**, trong đó 1 đang hiệu lực. Hợp đồng sắp hết hạn nhất là ô **HC-08**, kết thúc ngày **20/10/2026**.",
            ChatbotSummaries.Lead([Live("vendor.contracts",
                Card("Hợp đồng #1", ("Thông tin", "HC-08"), ("Trạng thái ghi nhận", "Đang hiệu lực"), ("Ngày kết thúc", "20/10/2026")),
                Card("Hợp đồng #2", ("Thông tin", "HC-09"), ("Trạng thái ghi nhận", "Đã hết hạn"), ("Ngày kết thúc", "01/01/2026")))]));
        Assert.Equal("Hiện bạn **không có khoản phí hay tiền phạt nào phải trả**.",
            ChatbotSummaries.Lead([Live("vendor.finance", Card("Tài chính", ("Tổng phải trả", "0 VND")))]));
        Assert.Null(ChatbotSummaries.Lead([Live("help.search", Card("x"))]));
    }

    [Theory]
    [InlineData("mỳ quảng", "mì quảng")]
    [InlineData("Chả giò", "nem rán")]
    [InlineData("cafe", "cà phê")]
    public void Curated_dish_names_expand_only_exact_entries(string query, string expected)
    {
        Assert.Contains(expected, ChatbotFoodPhoto.OtherNames(query));
        Assert.Empty(ChatbotFoodPhoto.OtherNames("mì quảng gà"));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", ChatbotFoodPhoto.SynonymsVersion);
    }

    [Fact]
    public async Task Regional_name_is_searched_when_the_given_name_finds_nothing()
    {
        using var f = new Fixture();
        f.Sender.Setup(s => s.Send(It.IsAny<SearchMarketplaceMenuQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SearchMarketplaceMenuQuery q, CancellationToken _) => q.Query == "mì quảng" ? [Dish(1, "Mì Quảng gà")] : Array.Empty<MarketplaceMenuItemDto>());
        f.Sender.Setup(s => s.Send(It.IsAny<ListStorefrontsQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync([Store(1, true, null)]);
        f.Sender.Setup(s => s.Send(It.Is<ListStorefrontsQuery>(q => q.Query == "mỳ quảng"), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var result = await f.Service().SendAsync("conversation", f.Request with { Content = "Tôi muốn ăn mỳ quảng" }, default);
        Assert.Contains(result.AssistantMessage.Cards, c => c.Fields.Any(x => x.Value == "Mì Quảng gà"));
        Assert.Contains(result.AssistantMessage.Sources, s => s.Title.Contains("tên gọi khác"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(12.0, "~50 m")]
    [InlineData(374.0, "~350 m")]
    [InlineData(1260.0, "~1,3 km")]
    public void Distance_labels_are_approximate(double? meters, string? expected) => Assert.Equal(expected, ChatbotTools.Distance(meters));
}
