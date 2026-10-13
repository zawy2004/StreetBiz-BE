using MediatR;
using Moq;
using System.Text.Json;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Application.DTOs.Commerce;
using StreetBiz.Application.Features.Chatbot;

namespace StreetBiz.Application.Tests;

public sealed class ChatbotServiceTests
{
    [Fact]
    public async Task Explicit_buyer_dish_request_returns_real_cards_without_a_provider_call()
    {
        using var f = new Fixture(); var model = new Model("GROQ");
        f.Sender.Setup(s => s.Send(It.IsAny<SearchMarketplaceMenuQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarketplaceMenuItemDto[] { new(7, 8, "Bún chả Quầy A", "Bún chả", null,
                "/api/uploads/menu-images/1/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png", 35000, "AVAILABLE", 1, "Bún") });
        f.Sender.Setup(s => s.Send(It.IsAny<ListStorefrontsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<StorefrontSummaryDto>());
        var result = await f.Service(model).SendAsync("conversation", f.Request with { Content = "Tôi muốn ăn bún chả" }, default);
        Assert.Equal("COMPLETED", result.AssistantMessage.Status);
        Assert.Equal(0, model.Calls);
        var card = Assert.Single(result.AssistantMessage.Cards);
        Assert.Equal("Bún chả Quầy A", card.Title);
        Assert.NotNull(card.ImageUrl);
        Assert.Contains(card.Fields, field => field.Label == "Giá niêm yết" && field.Value.Contains("35.000"));
        Assert.Contains(result.AssistantMessage.Actions, a => a.Route == "/customer/explore/stores/8");
    }

    [Theory]
    [InlineData("CUSTOMER", "Tôi muốn ăn bún chả", "bún chả")]
    [InlineData("CUSTOMER", "toi muon an bun cha", "bun cha")]
    [InlineData("CUSTOMER", "Tôi muốn ăn bún chả ở gần đây", null)]
    [InlineData("CUSTOMER", "Tôi muốn ăn gì?", null)]
    [InlineData("VENDOR", "Tôi muốn ăn bún chả", null)]
    public void Simple_food_intent_preserves_constraints_and_role(string role, string question, string? expected)
        => Assert.Equal(expected, ChatbotIntent.FoodQuery(role, question));
    [Fact]
    public async Task Long_history_is_trimmed_before_rejecting_a_short_question()
    {
        using var f = new Fixture(); f.Settings.MaxContextCharacters = 10000;
        var history = Enumerable.Range(1, 8).Select(i => f.User with {
            Id = "history-" + i, Sender = i % 2 == 0 ? "ASSISTANT" : "USER", Content = new string('ấ', 1500), Ordinal = i
        }).ToArray();
        f.Store.Setup(s => s.HistoryAsync(It.IsAny<ChatbotActor>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatbotPage<ChatbotMessage>(history, null));
        var model = new Model("GROQ");
        var result = await f.Service(model).SendAsync("conversation", f.Request, default);
        Assert.Equal("COMPLETED", result.AssistantMessage.Status);
        Assert.InRange(model.LastRequest!.History.Count, 2, 6);
    }
    [Fact]
    public async Task Successful_single_answer_is_not_generated_twice_and_usage_is_actual()
    {
        using var f = new Fixture(); var model = new Model("GROQ");
        var result = await f.Service(model).SendAsync("conversation", f.Request, default);
        Assert.Equal("COMPLETED", result.AssistantMessage.Status);
        Assert.Equal(1, model.Calls);
        f.Store.Verify(s => s.SaveAsync(It.IsAny<ChatbotActor>(), It.Is<ChatbotMessage>(m => m.Status == "COMPLETED"),
            "GROQ", "test", new ChatbotUsage(40, 5), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Photo_search_uses_public_queries_and_compares_only_returned_public_images()
    {
        using var f = new Fixture(); f.Settings.AttachmentsEnabled = true;
        var userBytes = new byte[] { 1, 2 }; var publicBytes = new byte[] { 3, 4 };
        f.Images = [new(userBytes, "image/png")];
        f.Sender.Setup(s => s.Send(It.IsAny<SearchMarketplaceMenuQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarketplaceMenuItemDto[] { new(7, 8, "Quầy A", "Bún chả", null,
                "/api/uploads/menu-images/1/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png", 30000, "AVAILABLE", 1, "Bún") });
        f.Sender.Setup(s => s.Send(It.IsAny<ListStorefrontsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<StorefrontSummaryDto>());
        f.PublicImages.Setup(p => p.ReadAsync(It.IsAny<IReadOnlyList<ChatbotPublicImage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatbotImage[] { new(publicBytes, "image/png", "Ảnh niêm yết: Bún chả; quầy A; ID 8") });
        var model = new FoodModel();
        var result = await f.Service(model).SendAsync("conversation", f.Request with {
            Content = "So sánh ảnh này với ảnh thật của quầy bán món đó", AttachmentIds = [Guid.NewGuid().ToString()]
        }, default);
        Assert.Equal("COMPLETED", result.AssistantMessage.Status);
        Assert.Equal(2, model.Calls); Assert.Equal(2, model.Final!.Images!.Count);
        Assert.Equal("/customer/explore/stores/8", result.AssistantMessage.Actions.First(a => a.Id.StartsWith("public.food:")).Route);
        Assert.Contains(result.AssistantMessage.Cards, c => c.Title == "Quầy A");
        Assert.Contains(result.AssistantMessage.Sources, s => s.Kind == "IMAGE_ANALYSIS");
        f.Sender.Verify(s => s.Send(It.Is<SearchMarketplaceMenuQuery>(q => q.Query == "bún chả" && q.Take == 16), It.IsAny<CancellationToken>()), Times.Once);
        Assert.All(userBytes.Concat(publicBytes), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_food_photo_uses_one_vision_call_and_broadens_only_if_empty(bool exactFound)
    {
        using var f = new Fixture(); f.Settings.AttachmentsEnabled = true;
        f.Images = [new([1, 2], "image/png")];
        var dish = new MarketplaceMenuItemDto(7, 8, "Quầy bánh mì", "Bánh mì", null, null, 20000, "AVAILABLE", 1, "Bánh mì");
        f.Sender.Setup(s => s.Send(It.IsAny<SearchMarketplaceMenuQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SearchMarketplaceMenuQuery q, CancellationToken _) => exactFound || q.Query == "bánh mì"
                ? new[] { dish } : Array.Empty<MarketplaceMenuItemDto>());
        f.Sender.Setup(s => s.Send(It.IsAny<ListStorefrontsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<StorefrontSummaryDto>());
        var model = new FoodModel("bánh mì xíu mại");
        var result = await f.Service(model).SendAsync("conversation", f.Request with {
            Content = "Tôi muốn ăn món giống hình", AttachmentIds = [Guid.NewGuid().ToString()]
        }, default);
        Assert.Equal("COMPLETED", result.AssistantMessage.Status); Assert.Equal(1, model.Calls);
        Assert.Contains("Ảnh có thể là", result.AssistantMessage.Content);
        Assert.Single(result.AssistantMessage.Cards, c => c.Kind == "status");
        Assert.Single(result.AssistantMessage.Cards, c => c.Kind == ChatbotFoodPhoto.CandidatesKind);
        if (!exactFound) Assert.Contains("không bảo đảm bán đúng biến thể", result.AssistantMessage.Content);
        f.PublicImages.Verify(p => p.ReadAsync(It.IsAny<IReadOnlyList<ChatbotPublicImage>>(), It.IsAny<CancellationToken>()), Times.Never);
        f.Sender.Verify(s => s.Send(It.Is<SearchMarketplaceMenuQuery>(q => q.Query == "bánh mì"), It.IsAny<CancellationToken>()),
            exactFound ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task Failed_photo_comparison_keeps_public_cards_and_drops_failed_partial_text()
    {
        using var f = new Fixture(); f.Settings.AttachmentsEnabled = true; f.Images = [new([1, 2], "image/png")];
        f.Sender.Setup(s => s.Send(It.IsAny<SearchMarketplaceMenuQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new MarketplaceMenuItemDto(7, 8, "Quầy A", "Bún chả", null, null, 30000, "AVAILABLE", 1, "Bún") });
        f.Sender.Setup(s => s.Send(It.IsAny<ListStorefrontsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<StorefrontSummaryDto>());
        var model = new FoodModel(failFinal: true);
        var result = await f.Service(model).SendAsync("conversation", f.Request with {
            Content = "So sánh ảnh với quầy", AttachmentIds = [Guid.NewGuid().ToString()]
        }, default);
        Assert.Equal("FAILED", result.AssistantMessage.Status); Assert.Equal("incomplete_response", result.AssistantMessage.Error!.Code);
        Assert.Equal(3, model.Calls); Assert.Empty(result.AssistantMessage.Content); Assert.Single(result.AssistantMessage.Cards);
        Assert.Contains(result.AssistantMessage.Actions, a => a.Route == "/customer/explore/stores/8");
        f.Events.Verify(e => e.PublishAsync(It.IsAny<ChatbotActor>(), It.Is<ChatbotEvent>(e => e.Type == "delta"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("incomplete_response", true, 2)]
    [InlineData("provider_timeout", true, 2)]
    [InlineData("unexpected_tool_call", true, 2)]
    [InlineData("malformed_function_call", true, 2)]
    [InlineData("blocked_response", false, 1)]
    [InlineData("provider_quota", true, 1)]
    public async Task Image_retry_does_not_append_partial_text_or_retry_safety_and_quota(string category, bool transient, int expected)
    {
        using var f = new Fixture(); f.Settings.AttachmentsEnabled = true; f.Images = [new([1], "image/png")];
        f.Actors.Setup(a => a.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ChatbotActor(1, 2, "VENDOR", null, 1));
        var model = new Mock<IChatbotModel>(); model.SetupGet(m => m.Name).Returns("GEMINI"); model.SetupGet(m => m.Available).Returns(true);
        var attempts = 0;
        model.Setup(m => m.GenerateAsync(It.IsAny<ChatbotProviderRequest>(), It.IsAny<Func<string, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ChatbotProviderRequest _, Func<string, CancellationToken, Task> delta, CancellationToken ct) => {
                attempts++; await delta(attempts == 1 ? "Đoạn dở dang" : "Câu trả lời hoàn chỉnh", ct);
                if (attempts == 1) throw new ChatbotProviderException(category, transient);
                return new ChatbotProviderResult("GEMINI", "test", "Câu trả lời hoàn chỉnh", [], null, new(20, 10));
            });
        var result = await f.Service(model.Object).SendAsync("conversation", f.Request with { AttachmentIds = [Guid.NewGuid().ToString()] }, default);
        Assert.Equal(expected, attempts); Assert.DoesNotContain("Đoạn dở dang", result.AssistantMessage.Content);
        Assert.Equal(expected == 2 ? "COMPLETED" : "FAILED", result.AssistantMessage.Status);
        if (expected == 2) Assert.Equal("Câu trả lời hoàn chỉnh", result.AssistantMessage.Content);
        Assert.All(f.Images[0].Bytes, b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("bánh mì xíu mại", "bánh mì")]
    [InlineData("bún chả Hà Nội", "bún chả")]
    [InlineData("bánh mì", null)]
    [InlineData("bánh mì không thịt", null)]
    [InlineData("món không rõ", null)]
    public void Broader_search_preserves_negative_constraints(string query, string? expected)
        => Assert.Equal(expected, ChatbotFoodPhoto.BroaderQuery(query));

    [Fact]
    public async Task Cancelling_image_request_does_not_start_another_provider_attempt()
    {
        using var f = new Fixture(); using var abort = new CancellationTokenSource();
        f.Settings.AttachmentsEnabled = true; f.Images = [new([1, 2], "image/png")];
        var model = new Mock<IChatbotModel>(); model.SetupGet(m => m.Name).Returns("GEMINI"); model.SetupGet(m => m.Available).Returns(true);
        model.Setup(m => m.GenerateAsync(It.IsAny<ChatbotProviderRequest>(), It.IsAny<Func<string, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns((ChatbotProviderRequest _, Func<string, CancellationToken, Task> _, CancellationToken ct) => {
                abort.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException();
            });
        var result = await f.Service(model.Object).SendAsync("conversation", f.Request with { AttachmentIds = [Guid.NewGuid().ToString()] }, abort.Token);
        Assert.Equal("INTERRUPTED", result.AssistantMessage.Status);
        model.Verify(m => m.GenerateAsync(It.IsAny<ChatbotProviderRequest>(), It.IsAny<Func<string, CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.All(f.Images[0].Bytes, b => Assert.Equal(0, b));
    }

    internal sealed class FoodModel(string query = "bún chả", bool failFinal = false, object? arguments = null) : IChatbotModel
    {
        public string Name => "GEMINI"; public bool Available => true;
        public int Calls; public ChatbotProviderRequest? Final; public ChatbotProviderRequest? First;
        public async Task<ChatbotProviderResult> GenerateAsync(ChatbotProviderRequest request, Func<string, CancellationToken, Task> delta, CancellationToken ct)
        {
            Calls++;
            First ??= request;
            if (!request.FinalAnswer)
                return new(Name, "test", "", [new("food", "public.food", ChatbotJson.Serialize(arguments ?? new { query }))],
                    JsonSerializer.SerializeToElement(new { role = "model", parts = Array.Empty<object>() }), new(80, 10));
            Final = request; await delta("Ảnh có vẻ là bún chả. Quầy A có thông tin niêm yết liên quan; chưa xác nhận còn món.", ct);
            if (failFinal) throw new ChatbotProviderException("incomplete_response", true);
            return new(Name, "test", "", [], null, new(90, 20));
        }
    }
    [Fact]
    public async Task Image_is_analyzed_despite_text_intent_and_page_context_and_bytes_are_cleared()
    {
        using var f = new Fixture(); f.Settings.AttachmentsEnabled = true;
        var bytes = new byte[] { 1, 2, 3 }; f.Images = [new(bytes, "image/png")];
        var vision = new Model("GEMINI"); var text = new Model("GROQ");
        var result = await f.Service(text, vision).SendAsync("conversation", f.Request with {
            Content = "Tìm người bán trên bản đồ và giải thích món trong ảnh",
            PageContext = new("public.vendor", "1"), AttachmentIds = [Guid.NewGuid().ToString()], ResponseStyle = "steps"
        }, default);
        Assert.Equal("COMPLETED", result.AssistantMessage.Status);
        Assert.Equal(1, vision.Calls); Assert.Equal(0, text.Calls);
        Assert.False(vision.LastRequest!.FinalAnswer);
        Assert.Equal("public.food", Assert.Single(vision.LastRequest.Tools).Name);
        Assert.Contains("danh sách đánh số", vision.LastRequest.SystemPrompt);
        Assert.True(result.AssistantMessage.HasAttachments);
        Assert.Contains(result.AssistantMessage.Sources, s => s.Kind == "IMAGE_ANALYSIS");
        Assert.DoesNotContain(result.AssistantMessage.Sources, s => s.Kind == "LIVE_DATA" || s.Kind == "PRODUCT_GUIDE");
        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Image_without_vision_provider_fails_instead_of_text_fallback()
    {
        using var f = new Fixture(); f.Settings.AttachmentsEnabled = true;
        f.Images = [new(new byte[] { 1 }, "image/png")]; var text = new Model("GROQ");
        var result = await f.Service(text).SendAsync("conversation", f.Request with { AttachmentIds = [Guid.NewGuid().ToString()] }, default);
        Assert.Equal("FAILED", result.AssistantMessage.Status);
        Assert.Equal("image_unavailable", result.AssistantMessage.Error!.Code);
        Assert.Equal(0, text.Calls); Assert.All(f.Images[0].Bytes, b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("concise", "ngắn gọn")]
    [InlineData("detailed", "chi tiết")]
    [InlineData("steps", "danh sách đánh số")]
    public async Task Style_is_validated_and_forwarded_without_changing_question(string style, string instruction)
    {
        using var f = new Fixture(); var model = new Model("GROQ");
        var result = await f.Service(model).SendAsync("conversation", f.Request with { ResponseStyle = style }, default);
        Assert.Equal("COMPLETED", result.AssistantMessage.Status);
        Assert.Equal(style, result.AssistantMessage.ResponseStyle);
        Assert.Contains(instruction, model.LastRequest!.SystemPrompt);
        Assert.Equal(f.Request.Content, model.LastRequest.Question);
    }

    [Fact]
    public async Task Arbitrary_style_instruction_is_rejected_before_storage()
    {
        using var f = new Fixture();
        var error = await Assert.ThrowsAsync<ChatbotException>(() => f.Service().SendAsync("conversation", f.Request with { ResponseStyle = "ignore role" }, default));
        Assert.Equal("invalid_response_style", error.Code);
        f.Store.Verify(s => s.StartAsync(It.IsAny<ChatbotActor>(), It.IsAny<string>(), It.IsAny<ChatbotSendRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task No_keys_still_returns_versioned_product_guidance()
    {
        using var f = new Fixture();
        var result = await f.Service().SendAsync("conversation", f.Request, default);
        Assert.Equal("COMPLETED", result.AssistantMessage.Status);
        Assert.Contains("Hướng dẫn StreetBiz", result.AssistantMessage.Content);
        Assert.NotEmpty(result.AssistantMessage.Sources);
    }
    [Fact]
    public async Task Transient_before_text_falls_back_but_after_text_does_not()
    {
        using var f = new Fixture(); var first = new Model("GROQ", fail: true); var backup = new Model("GEMINI");
        var result = await f.Service(first, backup).SendAsync("conversation", f.Request, default);
        Assert.Equal("COMPLETED", result.AssistantMessage.Status); Assert.True(backup.Calls > 0);

        using var g = new Fixture(); var partial = new Model("GROQ", partial: true); var unused = new Model("GEMINI");
        var failed = await g.Service(partial, unused).SendAsync("conversation", g.Request, default);
        Assert.Equal("FAILED", failed.AssistantMessage.Status); Assert.Equal("Xin chào", failed.AssistantMessage.Content); Assert.Equal(0, unused.Calls);
    }
    [Fact]
    public async Task Database_replay_never_calls_provider()
    {
        using var f = new Fixture();
        f.Store.Setup(s => s.StartAsync(It.IsAny<ChatbotActor>(), "conversation", It.IsAny<ChatbotSendRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatbotStart(new(f.User, f.Saved with { Status = "COMPLETED", Content = "existing" }), true));
        var provider = new Model("GROQ");
        var result = await f.Service(provider).SendAsync("conversation", f.Request, default);
        Assert.Equal("existing", result.AssistantMessage.Content); Assert.Equal(0, provider.Calls);
    }
    [Fact]
    public async Task Revocation_during_generation_does_not_return_private_content()
    {
        using var f = new Fixture();
        var provider = new Model("GROQ", onFinal: () => f.Active = false);
        var error = await Assert.ThrowsAsync<ChatbotException>(() => f.Service(provider).SendAsync("conversation", f.Request, default));
        Assert.Equal(401, error.Status);
    }

    private sealed class Model(string name, bool fail = false, bool partial = false, Action? onFinal = null) : IChatbotModel
    {
        public int Calls; public ChatbotProviderRequest? LastRequest; public string Name => name; public bool Available => true;
        public async Task<ChatbotProviderResult> GenerateAsync(ChatbotProviderRequest request, Func<string, CancellationToken, Task> delta, CancellationToken ct)
        {
            LastRequest = request; Calls++; if (fail) throw new ChatbotProviderException("quota", true);
            if (request.FinalAnswer) { onFinal?.Invoke(); await delta("Xin chào", ct); if (partial) throw new ChatbotProviderException("connection_lost", true); }
            return new(Name, "test", !request.FinalAnswer && (partial || onFinal is not null) ? "" : "Xin chào", [], null, new(40, 5));
        }
    }
    internal sealed class Fixture : IDisposable
    {
        public bool Active = true;
        public readonly Mock<IChatbotListReader> Lists = new();
        public IReadOnlyList<ChatbotImage> Images = [];
        public readonly Mock<IChatbotActorResolver> Actors = new(); public readonly Mock<IChatbotStore> Store = new();
        public readonly Mock<ISender> Sender = new();
        public readonly Mock<IChatbotPublicImages> PublicImages = new();
        public readonly Mock<IChatbotEvents> Events = new();
        public readonly ChatbotSettings Settings = new(); public readonly ChatbotRuntime Runtime;
        public readonly ChatbotSendRequest Request = new(Guid.NewGuid().ToString(), "Giải thích cách sử dụng StreetBiz");
        public readonly ChatbotMessage User; public ChatbotMessage Saved;
        public Fixture()
        {
            Runtime = new(Settings);
            var actor = new ChatbotActor(1, 2, "CUSTOMER", null, null);
            Actors.Setup(a => a.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(actor);
            Actors.Setup(a => a.IsActiveAsync(It.IsAny<ChatbotActor>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Active);
            User = new("user", "conversation", Request.ClientRequestId, "USER", "COMPLETED", Request.Content, false, [], [], [], DateTimeOffset.UtcNow, null, 1, 1);
            Saved = User with { Id = "answer", Sender = "ASSISTANT", Status = "GENERATING", Content = "", Ordinal = 2, IsAiGenerated = true };
            Store.Setup(s => s.StartAsync(It.IsAny<ChatbotActor>(), "conversation", It.IsAny<ChatbotSendRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatbotStart(new(User, Saved), false));
            Store.Setup(s => s.IsGeneratingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Store.Setup(s => s.HistoryAsync(It.IsAny<ChatbotActor>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatbotPage<ChatbotMessage>([], null));
            Store.Setup(s => s.SaveAsync(It.IsAny<ChatbotActor>(), It.IsAny<ChatbotMessage>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<ChatbotUsage>(), It.IsAny<CancellationToken>()))
                .Callback((ChatbotActor _, ChatbotMessage m, string? _, string? _, ChatbotUsage _, CancellationToken _) => Saved = m).Returns(Task.CompletedTask);
            Store.Setup(s => s.MessageAsync(It.IsAny<ChatbotActor>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Saved);
        }
        public ChatbotService Service(params IChatbotModel[] models)
        {
            var attachments = new Mock<IChatbotAttachments>();
            attachments.Setup(a => a.ReadAsync(It.IsAny<ChatbotActor>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Images);
            return new(Actors.Object, Store.Object, new(Sender.Object, Actors.Object, new(), TimeProvider.System, Lists.Object), new(), models,
                Events.Object, Runtime, Settings, TimeProvider.System, attachments.Object, PublicImages.Object);
        }
        public void Dispose() => Runtime.Dispose();
    }
}
