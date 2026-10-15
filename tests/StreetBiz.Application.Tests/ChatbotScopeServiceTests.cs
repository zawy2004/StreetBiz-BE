using Moq;
using StreetBiz.Application.Features.Chatbot;

namespace StreetBiz.Application.Tests;

/// <summary>An off-topic turn costs nothing: no model, no data, a fixed reply that is saved as a normal turn.</summary>
public sealed class ChatbotScopeServiceTests
{
    [Fact]
    public async Task An_off_topic_question_is_refused_without_a_model_call_or_a_lookup()
    {
        using var f = new ChatbotServiceTests.Fixture();
        var model = new CountingModel();

        var result = await f.Service(model).SendAsync(
            "conversation", f.Request with { Content = "Hôm nay thời tiết Đà Nẵng thế nào?" }, default);

        Assert.Equal("COMPLETED", result.AssistantMessage.Status);
        Assert.Equal(0, model.Calls);
        Assert.Empty(f.Sender.Invocations);
        Assert.Contains("chỉ hỗ trợ các nội dung liên quan đến website", f.Saved.Content);
        // Nothing the model or a tool could have attached: no guide source, no cards.
        Assert.Empty(f.Saved.Sources);
        Assert.Empty(f.Saved.Cards);
        // Still saved with no provider and no tokens spent.
        f.Store.Verify(s => s.SaveAsync(
            It.IsAny<ChatbotActor>(), It.Is<ChatbotMessage>(m => m.Status == "COMPLETED"),
            null, null, new ChatbotUsage(0, 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_site_question_still_reaches_the_model()
    {
        using var f = new ChatbotServiceTests.Fixture();
        var model = new CountingModel();

        var result = await f.Service(model).SendAsync("conversation", f.Request, default);

        Assert.Equal("COMPLETED", result.AssistantMessage.Status);
        Assert.Equal(1, model.Calls);
        // And the model is told the same rule the gate enforces.
        Assert.Contains("chỉ hỗ trợ nội dung liên quan đến StreetBiz", model.Prompt);
    }

    [Fact]
    public async Task A_short_follow_up_goes_through_once_there_is_an_answered_turn()
    {
        using var f = new ChatbotServiceTests.Fixture();
        var answered = f.User with { Id = "earlier", Sender = "ASSISTANT", Status = "COMPLETED", Content = "Phí tháng này là…", Ordinal = 1 };
        f.Store.Setup(s => s.HistoryAsync(It.IsAny<ChatbotActor>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatbotPage<ChatbotMessage>([answered], null));
        var model = new CountingModel();

        await f.Service(model).SendAsync("conversation", f.Request with { Content = "Còn tháng sau thì sao?" }, default);

        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task The_same_follow_up_as_a_first_message_is_refused()
    {
        using var f = new ChatbotServiceTests.Fixture();
        var model = new CountingModel();

        await f.Service(model).SendAsync("conversation", f.Request with { Content = "Còn tháng sau thì sao?" }, default);

        Assert.Equal(0, model.Calls);
        Assert.Contains("chỉ hỗ trợ", f.Saved.Content);
    }

    [Fact]
    public void A_guest_off_topic_question_gets_the_refusal_and_no_guide_attached()
    {
        using var f = new ChatbotServiceTests.Fixture();
        f.Settings.GuestEnabled = true;

        var reply = f.Service().Guest(new ChatbotGuestRequest("Kể cho tôi một câu chuyện cười"));

        Assert.Contains("chỉ hỗ trợ các nội dung liên quan đến website", reply.Content);
        Assert.Empty(reply.Sources);
    }

    [Fact]
    public void A_guest_site_question_still_gets_the_guide()
    {
        using var f = new ChatbotServiceTests.Fixture();
        f.Settings.GuestEnabled = true;

        var reply = f.Service().Guest(new ChatbotGuestRequest("Làm sao đăng ký tài khoản StreetBiz?"));

        Assert.StartsWith("Hướng dẫn StreetBiz", reply.Content);
        Assert.NotEmpty(reply.Sources);
    }

    private sealed class CountingModel : IChatbotModel
    {
        public int Calls;
        public string Prompt = "";
        public string Name => "GROQ";
        public bool Available => true;

        public async Task<ChatbotProviderResult> GenerateAsync(
            ChatbotProviderRequest request, Func<string, CancellationToken, Task> delta, CancellationToken ct)
        {
            Calls++;
            Prompt = request.SystemPrompt;
            if (request.FinalAnswer) await delta("Xin chào", ct);
            return new(Name, "test", request.FinalAnswer ? "" : "Xin chào", [], null, new(40, 5));
        }
    }
}
