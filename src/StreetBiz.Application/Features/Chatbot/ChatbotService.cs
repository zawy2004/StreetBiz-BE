using System.Text;
using System.Text.Json;
using StreetBiz.Application.Common.Exceptions;

namespace StreetBiz.Application.Features.Chatbot;

public sealed class ChatbotService(IChatbotActorResolver actors, IChatbotStore store, ChatbotTools tools,
    ChatbotKnowledge knowledge, IEnumerable<IChatbotModel> providers, IChatbotEvents events,
    ChatbotRuntime runtime, ChatbotSettings settings, TimeProvider clock, IChatbotAttachments attachments,
    IChatbotPublicImages? publicImages = null)
{
    private DateTimeOffset Now => clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7));

    public void RequireEnabled()
    {
        if (!settings.Enabled) throw new ChatbotException(503, "disabled", "Trợ lý đang tạm nghỉ. Bạn vẫn có thể dùng các chức năng trên web.");
    }

    public async Task<ChatbotSendResult> SendAsync(string conversationId, ChatbotSendRequest original, CancellationToken requestAborted)
    {
        RequireEnabled();
        ChatbotPrivacy.Validate(original, settings);
        var actor = await actors.RequireAsync(requestAborted);
        var request = original with { Content = ChatbotPrivacy.Text(original.Content.Trim(), settings.MaxQuestionCharacters), ClientRequestId = Guid.Parse(original.ClientRequestId).ToString("D") };
        var contextTool = ChatbotNavigation.ContextTool(actor.Role, request.PageContext);
        if (request.AttachmentIds is { Length: > 0 } && !settings.AttachmentsEnabled)
            throw new ChatbotException(400, "attachments_disabled", "Ảnh chưa được bật. Vui lòng mô tả bằng văn bản; không gửi giấy tờ định danh.");
        using var admission = runtime.Enter(actor.UserId);
        // Position is per-request input, not part of the replay identity: a retry from a few metres away is the same turn.
        var started = await store.StartAsync(actor, conversationId, request with { Location = null }, requestAborted);
        if (started.IsReplay) return started.Messages;
        var message = started.Messages.AssistantMessage with { HasAttachments = request.AttachmentIds is { Length: > 0 }, ResponseStyle = request.ResponseStyle };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TurnTimeoutSeconds));
        using var registered = runtime.Register(message.Id, deadline);
        var ct = deadline.Token;
        var sequence = 0L;
        var startedAt = clock.GetUtcNow();
        var body = new StringBuilder();
        var pending = new StringBuilder();
        var lastFlush = clock.GetUtcNow();
        var lastSave = clock.GetUtcNow();
        var lastAccess = DateTimeOffset.MinValue;
        var usage = new ChatbotUsage(0, 0);
        var reservedInput = 0;
        var reservedOutput = 0;
        var evidence = new List<ChatbotEvidence>();
        var exchanges = new List<ChatbotToolExchange>();
        IReadOnlyList<ChatbotImage> images = [];
        string? providerName = null, modelId = null;

        async Task EnsureAccess(CancellationToken token, bool force = false)
        {
            if (!force && clock.GetUtcNow() - lastAccess < TimeSpan.FromMilliseconds(500)) return;
            if (!await actors.IsActiveAsync(actor, token)) throw new ChatbotException(401, "session_expired", "Phiên đăng nhập không còn hiệu lực.");
            if (!await store.IsGeneratingAsync(conversationId, message.Id, token)) throw new OperationCanceledException(token);
            lastAccess = clock.GetUtcNow();
        }
        async Task Emit(string type, object payload, CancellationToken token)
        {
            await events.PublishAsync(actor, new(Guid.NewGuid().ToString("N"), conversationId, message.Id, request.ClientRequestId,
                1, ++sequence, message.Version, Now, type, payload), token);
        }
        async Task Flush(CancellationToken token, bool force = false)
        {
            if (pending.Length == 0 || (!force && clock.GetUtcNow() - lastFlush < TimeSpan.FromMilliseconds(80))) return;
            await EnsureAccess(token);
            var delta = pending.ToString(); pending.Clear();
            message = message with { Content = body.ToString(), Version = message.Version + 1 };
            await Emit("delta", new { delta }, token);
            lastFlush = clock.GetUtcNow();
            if (lastFlush - lastSave > TimeSpan.FromSeconds(1))
            {
                await store.SaveAsync(actor, message, providerName, modelId, usage, token);
                lastSave = lastFlush;
            }
        }
        async Task Delta(string text, CancellationToken token)
        {
            await EnsureAccess(token);
            if (body.Length == 0 && text.Length > 0) ChatbotTelemetry.FirstDeltaSeconds.Record((clock.GetUtcNow() - startedAt).TotalSeconds, new KeyValuePair<string, object?>("role", actor.Role));
            body.Append(text); pending.Append(text);
            await Flush(token);
        }
        async Task<ChatbotEvidence> Execute(string tool, string args)
        {
            await EnsureAccess(ct);
            await Emit("status", new { code = "RETRIEVING", label = "Đang tra cứu dữ liệu được phép xem…" }, ct);
            try
            {
                var result = await tools.ExecuteAsync(actor, tool, args, request.Content, ct, request.Location);
                evidence.Add(result);
                await store.AuditToolAsync(actor, message.Id, tool, "SUCCESS", ct);
                return result;
            }
            catch (Exception ex) when (ex is AppException or ChatbotException)
            {
                await store.AuditToolAsync(actor, message.Id, tool, "DENIED_OR_UNAVAILABLE", ct);
                // Do not send exception internals or existence/ownership details to the model.
                throw new ChatbotException(404, "resource_unavailable", "Không lấy được dữ liệu này trong phạm vi tài khoản của bạn. Hãy chọn lại hồ sơ hoặc mở màn hình nghiệp vụ.");
            }
        }

        try
        {
            await Emit("started", started.Messages, ct);
            evidence.Add(knowledge.Evidence(actor.Role, request.Content));
            images = await attachments.ReadAsync(actor, request.AttachmentIds ?? [], ct);
            if (images.Count > 0) evidence.Clear();
            // An image question must not be short-circuited by a text-only intent or page context.
            if (images.Count == 0)
            {
                if (ChatbotIntent.FoodQuery(actor.Role, request.Content) is { } dish)
                    await Execute("public.food", ChatbotJson.Serialize(new { query = dish }));
                else if (ChatbotIntent.SlotPermit(actor.Role, request.Content) is { } slotCode)
                    await Execute("ward.slot_permit", ChatbotJson.Serialize(new { slotCode }));
                else if (contextTool is { } context) await Execute(context.Tool, context.Arguments);
                else if (ChatbotIntent.Tool(actor.Role, request.Content) is { } intent) await Execute(intent, "{}");
            }
            var history = (await store.HistoryAsync(actor, conversationId, message.Ordinal - 1, ct)).Items
                .Where(m => m.Status == "COMPLETED")
                .TakeLast(8).Select(m => new ChatbotHistoryEntry(m.Sender.ToLowerInvariant(),
                    m.Sources.Any(s => s.Kind == "LIVE_DATA") && !m.Sources.Any(s => s.Id.StartsWith("live:public.food:", StringComparison.Ordinal))
                        ? "Các đối tượng đã xem (phải tra cứu lại trạng thái): " + string.Join("; ", m.Cards.Take(5).Select(c => c.Title))
                        : ChatbotPrivacy.Text(m.Content, 1500))).ToArray();
            if (images.Count > 0) history = history.TakeLast(2).ToArray();
            var comparePhotos = images.Count > 0 && ChatbotFoodPhoto.NeedsComparison(request.Content);
            var complex = comparePhotos || request.Content.Length > 1200;
            var models = providers.Where(p => p.Available && (images.Count == 0 || p.Name == "GEMINI"))
                .OrderBy(p => p.Name == (images.Count > 0 ? "GEMINI" : "GROQ") ? 0 : 1).Take(settings.CrossProviderFallback ? 2 : 1).ToArray();
            var normalized = ChatbotKnowledge.Normalize(request.Content);
            var legal = normalized.Contains("dieu luat") || normalized.Contains("nghi dinh") || normalized.Contains("can cu phap ly") || normalized.Contains("muc phat");
            if (legal && evidence.All(e => e.Source.Kind != "LIVE_DATA"))
            {
                await Delta(ChatbotKnowledge.Guides.Single(g => g.Id == "legal").Text, ct);
            }
            else if (evidence.Any(e => e.Source.Kind == "LIVE_DATA"))
                await Delta(FactualAnswer(evidence, request.ResponseStyle), ct);
            else if (models.Length == 0)
            {
                if (images.Count > 0) throw new ChatbotException(503, "image_unavailable", "Chưa có dịch vụ xử lý ảnh. Hãy mô tả vấn đề bằng văn bản.");
                await Delta(BasicAnswer(actor, request.Content, evidence, request.ResponseStyle), ct);
            }
            else
            {
                var complete = false;
                var calls = evidence.Count(e => e.Source.Kind == "LIVE_DATA");
                var steps = 0;
                foreach (var model in models)
                {
                    providerName = model.Name;
                    exchanges.Clear();
                    try
                    {
                        var answered = false;
                        var availableTools = tools.Definitions(actor.Role, photo: images.Count > 0)
                            .Where(t => images.Count == 0 || t.Name == "public.food").ToArray();
                        if (images.Count > 0)
                            await Emit("status", new { code = "ANALYZING_IMAGE", label = "Đang nhận diện ảnh và nội dung câu hỏi…" }, ct);
                        while (availableTools.Length > 0 && steps < settings.MaxModelSteps - 1)
                        {
                            await EnsureAccess(ct);
                            var input = new ChatbotProviderRequest(ChatbotKnowledge.SystemPrompt(actor) + "\n" + ChatbotPresentation.Instruction(request.ResponseStyle), history, request.Content,
                                availableTools, evidence, exchanges, false, complex, images);
                            input = Reserve(input);
                            var selection = await Generate(model, input, (_, _) => Task.CompletedTask);
                            steps++; modelId = selection.Model;
                            Charge(selection.Usage);
                            if (selection.ToolCalls.Count == 0)
                            {
                                if (!string.IsNullOrWhiteSpace(selection.Text)
                                    && evidence.All(e => e.Source.Kind != "LIVE_DATA" || e.Tool == "public.food"))
                                {
                                    await Delta(selection.Text, ct);
                                    answered = true;
                                }
                                break;
                            }
                            if (selection.ToolCalls.Count + calls > settings.MaxToolCalls)
                                throw new ChatbotException(422, "tool_budget", "Câu hỏi cần quá nhiều lượt tra cứu. Hãy chia nhỏ theo từng hồ sơ.");
                            var replies = new List<ChatbotToolReply>();
                            foreach (var call in selection.ToolCalls)
                            {
                                if (!availableTools.Any(t => t.Name == call.Name))
                                    throw new ChatbotException(403, "tool_denied", "Chức năng này không được phép trong lượt hỏi hiện tại.");
                                calls++;
                                var result = await Execute(call.Name, call.Arguments);
                                replies.Add(new(call.Id, call.Name, result.Json));
                                if (images.Count > 0 && call.Name == "public.food" && !comparePhotos)
                                {
                                    var query = ChatbotJson.Read<JsonElement>(call.Arguments).GetProperty("query").GetString()!.Trim();
                                    var hint = ChatbotFoodPhoto.Hint(call.Arguments);
                                    string? broader = null;
                                    if (result.Cards.Count == 0 && calls < settings.MaxToolCalls
                                        && ChatbotFoodPhoto.BroaderQuery(query) is { } broad)
                                    {
                                        calls++;
                                        result = await Execute("public.food", ChatbotJson.Serialize(new { query = broad }));
                                        broader = broad;
                                    }
                                    var found = result.Cards.Count > 0;
                                    foreach (var alternative in hint.Alternatives)
                                    {
                                        if (calls >= settings.MaxToolCalls) break;
                                        calls++;
                                        found |= (await Execute("public.food", ChatbotJson.Serialize(new { query = alternative }))).Cards.Count > 0;
                                    }
                                    evidence.Add(ChatbotFoodPhoto.Candidates(hint, Now));
                                    await Delta(ChatbotFoodPhoto.Answer(query, found, broader, hint.Alternatives) + "\n\n", ct);
                                    answered = true;
                                }
                                if (comparePhotos && publicImages is not null && result.PublicImages is { Count: > 0 })
                                {
                                    await Emit("status", new { code = "COMPARING_IMAGES", label = "Đang đối chiếu ảnh món/quầy công khai…" }, ct);
                                    var references = await publicImages.ReadAsync(result.PublicImages.Take(Math.Max(0, 4 - images.Count)).ToArray(), ct);
                                    images = images.Concat(references).ToArray();
                                }
                            }
                            if (selection.NativeAssistant is { } native) exchanges.Add(new(model.Name, native, replies));
                            if (evidence.Any(e => e.Authoritative || e.Tool == "public.food")) break;
                        }
                        if (answered) { }
                        else if (evidence.Any(e => e.Source.Kind == "LIVE_DATA" && e.Tool != "public.food"))
                        {
                            // Live amounts/status/permit claims are formatted from verified data, never guessed by an LLM.
                            await Delta(FactualAnswer(evidence, request.ResponseStyle), ct);
                        }
                        else
                        {
                            if (steps >= settings.MaxModelSteps) throw new ChatbotException(422, "model_budget", "Hãy chia câu hỏi thành các phần ngắn hơn.");
                            await Emit("status", new { code = "GENERATING", label = "Đang soạn câu trả lời…" }, ct);
                            var input = new ChatbotProviderRequest(ChatbotKnowledge.SystemPrompt(actor) + "\n" + ChatbotPresentation.Instruction(request.ResponseStyle), history, request.Content,
                                [], evidence, exchanges, true, complex, images);
                            input = Reserve(input);
                            var result = await Generate(model, input, Delta);
                            steps++; modelId = result.Model; Charge(result.Usage);
                        }
                        if (images.Count > 0)
                            evidence.Add(new("image.analysis", "{}", new("image-analysis", "Nhận định AI từ ảnh bạn gửi — chưa được xác minh", "IMAGE_ANALYSIS", Now, null, null), [], [], false));
                        complete = true; break;
                    }
                    catch (ChatbotProviderException ex) when (ex.Transient && body.Length == 0 && model != models.Last())
                    {
                        ChatbotTelemetry.Fallbacks.Add(1, new KeyValuePair<string, object?>("provider", model.Name));
                        await Emit("status", new { code = "RETRYING", label = "Dịch vụ đang bận, đang thử kết nối dự phòng…" }, ct);
                    }
                }
                if (!complete) throw new ChatbotException(503, "provider_unavailable", "Trợ lý chưa thể trả lời lúc này. Bạn có thể thử lại hoặc mở chức năng bên dưới.");
            }
            await Flush(ct, true);
            await EnsureAccess(ct, true);
            message = message with { Content = evidence.Any(e => e.Source.Kind == "LIVE_DATA" && e.Tool != "public.food") ? body.ToString() : ChatbotPrivacy.Text(body.ToString(), settings.MaxOutputTokens * 16), Status = "COMPLETED",
                CompletedAt = Now, Version = message.Version + 1, Checklist = ChatbotPresentation.Checklist(evidence),
                Sources = evidence.Select(e => e.Source).DistinctBy(s => s.Id).ToArray(),
                Cards = evidence.SelectMany(e => e.Cards).Take(30).ToArray(),
                Actions = evidence.SelectMany(e => e.Actions).Concat(ChatbotNavigation.ForRole(actor.Role)).DistinctBy(a => a.Id).Take(30).ToArray() };
            await store.SaveAsync(actor, message, providerName, modelId, usage, ct);
            var saved = await store.MessageAsync(actor, conversationId, message.Id, ct);
            await Emit(saved.Status == "COMPLETED" ? "completed" : "cancelled", saved, ct);
            return new(started.Messages.UserMessage, saved);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var cancelled = ex is OperationCanceledException;
            var error = ex is ChatbotException known
                ? new ChatbotError(known.Code, known.Message, known.Status is not (401 or 403))
                : ex is ChatbotProviderException provider ? ProviderError(provider)
                : new ChatbotError(cancelled ? "interrupted" : "provider_unavailable",
                    cancelled ? "Câu trả lời đã dừng hoặc quá thời gian. Bạn có thể thử lại." : "Không thể hoàn tất câu trả lời lúc này. Vui lòng thử lại.", true);
            message = message with { Content = ChatbotPrivacy.Text(body.ToString(), settings.MaxOutputTokens * 16), Status = cancelled ? "INTERRUPTED" : "FAILED",
                CompletedAt = Now, Version = message.Version + 1, Error = error,
                Cards = evidence.Where(e => e.Tool == "public.food").SelectMany(e => e.Cards).Take(30).ToArray(),
                Actions = evidence.Where(e => e.Tool == "public.food").SelectMany(e => e.Actions).DistinctBy(a => a.Id).Take(30).ToArray(),
                Sources = evidence.Where(e => e.Tool == "public.food").Select(e => e.Source).DistinctBy(s => s.Id).ToArray() };
            await store.SaveAsync(actor, message, providerName, modelId, usage, cleanup.Token);
            // Do not publish/return content after the caller's account/session lost access.
            if (!await actors.IsActiveAsync(actor, cleanup.Token)) throw new ChatbotException(401, "session_expired", "Phiên đăng nhập không còn hiệu lực.");
            var saved = await store.MessageAsync(actor, conversationId, message.Id, cleanup.Token);
            await Emit(saved.Status == "CANCELLED" ? "cancelled" : "failed", saved, cleanup.Token);
            return new(started.Messages.UserMessage, saved);
        }

        finally
        {
            foreach (var image in images) System.Security.Cryptography.CryptographicOperations.ZeroMemory(image.Bytes);
            var tags = new System.Diagnostics.TagList { { "role", actor.Role }, { "provider", providerName ?? "KB" }, { "status", message.Status } };
            ChatbotTelemetry.TurnSeconds.Record((clock.GetUtcNow() - startedAt).TotalSeconds, tags);
            ChatbotTelemetry.Turns.Add(1, tags);
            ChatbotTelemetry.Tokens.Add(usage.InputTokens + usage.OutputTokens, tags);
        }

        async Task<ChatbotProviderResult> Generate(IChatbotModel model, ChatbotProviderRequest input, Func<string, CancellationToken, Task> delta)
        {
            if (input.Images is not { Count: > 0 }) return await GenerateOnce(model, input, delta);
            for (var attempt = 0; ; attempt++)
            {
                var buffered = new StringBuilder();
                try
                {
                    var result = await GenerateOnce(model, input, (chunk, _) =>
                    {
                        buffered.Append(chunk);
                        if (buffered.Length > settings.MaxOutputTokens * 16)
                            throw new ChatbotProviderException("output_limit", false);
                        return Task.CompletedTask;
                    });
                    if (input.FinalAnswer)
                    {
                        var text = string.IsNullOrWhiteSpace(result.Text) ? buffered.ToString() : result.Text;
                        if (string.IsNullOrWhiteSpace(text)) throw new ChatbotProviderException("incomplete_response", true);
                        await delta(text, ct);
                    }
                    return result;
                }
                catch (ChatbotProviderException ex) when (attempt == 0 && !ct.IsCancellationRequested && ex.Transient
                    && ex.Category is "incomplete_response" or "network" or "provider_timeout" or "unexpected_tool_call" or "malformed_function_call")
                {
                    ChatbotTelemetry.Fallbacks.Add(1, new KeyValuePair<string, object?>("provider", model.Name));
                    await Emit("status", new { code = "RETRYING", label = "Phản hồi ảnh chưa hoàn tất, đang thử lại một lần…" }, ct);
                    input = Reserve(input);
                }
            }
        }

        async Task<ChatbotProviderResult> GenerateOnce(IChatbotModel model, ChatbotProviderRequest input, Func<string, CancellationToken, Task> delta)
        {
            using var callDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            callDeadline.CancelAfter(TimeSpan.FromSeconds(input.Images is { Count: > 0 }
                ? Math.Min(settings.ProviderTimeoutSeconds, settings.ImageProviderTimeoutSeconds) : settings.ProviderTimeoutSeconds));
            try
            {
                var result = await model.GenerateAsync(input, delta, callDeadline.Token);
                if (result.Text.Length > settings.MaxOutputTokens * 16) throw new ChatbotProviderException("output_limit", true);
                return result;
            }
            catch (ChatbotProviderException error)
            {
                ChatbotTelemetry.ProviderErrors.Add(1, new KeyValuePair<string, object?>("provider", model.Name),
                    new KeyValuePair<string, object?>("category", error.Category));
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                ChatbotTelemetry.ProviderErrors.Add(1, new KeyValuePair<string, object?>("provider", model.Name),
                    new KeyValuePair<string, object?>("category", "provider_timeout"));
                throw new ChatbotProviderException("provider_timeout", true);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
            {
                ChatbotTelemetry.ProviderErrors.Add(1, new KeyValuePair<string, object?>("provider", model.Name),
                    new KeyValuePair<string, object?>("category", "incomplete_response"));
                throw new ChatbotProviderException("incomplete_response", true);
            }
        }

        ChatbotProviderRequest Reserve(ChatbotProviderRequest input)
        {
            int Size(ChatbotProviderRequest value) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value with { Images = null },
                new JsonSerializerOptions(ChatbotJson.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            var size = Size(input);
            // Drop oldest context first; keep current evidence, permissions and the most recent pair.
            while (input.History.Count > 2 && (size > settings.MaxContextCharacters * 3
                || usage.InputTokens + usage.OutputTokens + (size + 2) / 3 + (input.Images?.Count ?? 0) * 4096 + settings.MaxOutputTokens > settings.MaxTurnTokenBudget))
            {
                input = input with { History = input.History.Skip(Math.Min(2, input.History.Count - 2)).ToArray() };
                size = Size(input);
            }
            var inputReserve = (size + 2) / 3 + (input.Images?.Count ?? 0) * 4096;
            if (size > settings.MaxContextCharacters * 3)
                throw new ChatbotException(422, "context_budget", "Nội dung tra cứu đã dài. Hãy hỏi tiếp bằng một cuộc trò chuyện mới hoặc chọn một hồ sơ.");
            if (usage.InputTokens + usage.OutputTokens + inputReserve + settings.MaxOutputTokens > settings.MaxTurnTokenBudget)
                throw new ChatbotException(422, "turn_budget", "Lượt này cần quá nhiều bước xử lý hoặc dữ liệu. Hãy thử lại với một yêu cầu cụ thể hơn; đây không phải hạn mức tài khoản.");
            // Conservative reservation remains charged on aborted/provider-failed calls.
            usage = new(usage.InputTokens + inputReserve, usage.OutputTokens + settings.MaxOutputTokens);
            reservedInput = inputReserve;
            reservedOutput = settings.MaxOutputTokens;
            return input;
        }
        // Metering is deliberately conservative even when a provider omits usage or a stream aborts.
        void Charge(ChatbotUsage next)
        {
            if (next.InputTokens > 0 && next.OutputTokens >= 0)
                usage = new(usage.InputTokens - reservedInput + next.InputTokens,
                    usage.OutputTokens - reservedOutput + next.OutputTokens);
            reservedInput = reservedOutput = 0;
        }
    }

    private static ChatbotError ProviderError(ChatbotProviderException error) => new(error.Category, error.Category switch
    {
        "provider_timeout" => "Dịch vụ AI phản hồi quá chậm. Bạn có thể thử lại; với ảnh, hãy chọn vùng cần hỏi rõ hơn.",
        "provider_quota" or "cooldown" => "Các kết nối AI khả dụng đang chạm giới hạn hoặc tạm nghỉ. Vui lòng thử lại sau ít phút.",
        "provider_configuration" or "provider_credentials" => "Kết nối AI chưa dùng được với cấu hình hiện tại. Vui lòng báo quản trị viên kiểm tra model và quyền API key.",
        "blocked_response" => "Dịch vụ AI không thể xử lý nội dung này. Hãy thử ảnh khác hoặc mô tả lại câu hỏi.",
        "output_limit" => "Câu trả lời vượt giới hạn một lượt. Hãy yêu cầu trả lời ngắn hơn.",
        "incomplete_response" => "Dịch vụ AI chưa trả về câu trả lời hoàn chỉnh. Bạn có thể thử lại hoặc hỏi bằng tên món.",
        "unexpected_tool_call" or "malformed_function_call" => "Dịch vụ AI gặp lỗi khi lập yêu cầu tra cứu. Bạn có thể thử lại hoặc nhập tên món.",
        _ => "Dịch vụ AI tạm thời không kết nối được. Vui lòng thử lại sau.",
    }, error.Transient);

    public ChatbotMessage Guest(ChatbotGuestRequest request)
    {
        RequireEnabled();
        if (!settings.GuestEnabled) throw new ChatbotException(403, "guest_disabled", "Vui lòng đăng nhập để dùng trợ lý.");
        ChatbotPrivacy.Validate(new(Guid.NewGuid().ToString(), request.Content, ResponseStyle: request.ResponseStyle), settings);
        var question = ChatbotPrivacy.Text(request.Content, settings.MaxQuestionCharacters);
        var evidence = knowledge.Evidence("GUEST", question);
        return new(Guid.NewGuid().ToString("N"), "guest", Guid.NewGuid().ToString(), "ASSISTANT", "COMPLETED",
            BasicAnswer(ChatbotActor.Guest, question, [evidence], request.ResponseStyle), true, [evidence.Source], [], ChatbotNavigation.ForRole("GUEST"), Now, Now, 0, 1, ResponseStyle: request.ResponseStyle);
    }

    private string BasicAnswer(ChatbotActor actor, string question, IReadOnlyList<ChatbotEvidence> evidence, string? style = null) =>
        evidence.Any(e => e.Source.Kind == "LIVE_DATA") ? FactualAnswer(evidence) :
        "Hướng dẫn StreetBiz:\n\n" + string.Join("\n\n", knowledge.Search(actor.Role, question, style == "detailed" ? 3 : 1).Select((g, i) => $"{(style == "steps" ? $"{i + 1}. " : "")}**{g.Title}**\n{g.Text}"))
        + (actor.UserId == 0 ? "\n\nĐăng nhập để tra cứu dữ liệu riêng theo vai trò của bạn." : "\n\nHiện đang dùng hướng dẫn cơ bản; chưa xác minh trạng thái riêng của bạn trong câu trả lời này.");

    private static string FactualAnswer(IReadOnlyList<ChatbotEvidence> evidence, string? style = null)
    {
        var live = evidence.Where(e => e.Source.Kind == "LIVE_DATA").ToArray();
        var cards = live.SelectMany(e => e.Cards).ToArray();
        if (live.All(e => e.Tool == "public.food"))
        {
            var stalls = cards.Select(c => c.Title).Distinct().Count();
            var open = cards.Where(c => c.Place?.IsOpenNow == true).Select(c => c.Title).Distinct().Count();
            return cards.Length == 0
                ? "Chưa tìm thấy quầy công khai niêm yết món này. Điều đó không có nghĩa là không ai bán; bạn có thể thử tên gọi khác hoặc mở Bản đồ người bán."
                : $"Tìm thấy **{stalls} quầy** niêm yết thông tin phù hợp" + (open > 0 ? $", trong đó {open} quầy đang trong giờ mở cửa." : ".")
                    + " Chạm vào thẻ để xem nhanh ảnh, giá và vị trí.\n\nThông tin niêm yết tại lúc tra cứu; chưa xác nhận quầy còn món hôm nay.";
        }
        if (cards.Length == 0)
            return live.Any(e => e.Tool == "ward.slot_permit")
                ? "Không tìm thấy ô này trong phường được phân công cho bạn. Hãy kiểm tra lại mã ô hoặc mở Tuần tra để quét QR tại chỗ.\n\nDữ liệu phản ánh thời điểm tra cứu."
                : "Lần tra cứu này không có bản ghi phù hợp trong phạm vi tài khoản của bạn. Bạn có thể mở màn hình nghiệp vụ để kiểm tra bộ lọc.";
        var summary = ChatbotSummaries.Lead(live)
            ?? (cards.Length == 1
                ? "Đã tra cứu thông tin được phép xem. Chi tiết nằm trong thẻ bên dưới."
                : $"Đã tra cứu {cards.Length} mục trong phạm vi tài khoản của bạn. Đây là danh sách có giới hạn, không phải tổng toàn hệ thống. Chọn **Mở chi tiết** ở thẻ tương ứng để tiếp tục.");
        var next = live.Any(e => e.Tool.StartsWith("vendor.registr", StringComparison.Ordinal))
            ? "Mở hồ sơ để xem yêu cầu bổ sung hoặc lý do xử lý. Hồ sơ đã được duyệt mới đáp ứng điều kiện xét duyệt thuê ô; đăng ký không tự tạo đơn thuê."
            : live.Any(e => e.Tool is "vendor.finance" or "vendor.fees" or "vendor.penalties")
                ? "Mở Tài chính để kiểm tra từng khoản và hạn thanh toán. Trợ lý không tạo giao dịch; trạng thái thanh toán chỉ được xác nhận bởi máy chủ."
            : live.Any(e => e.Tool.StartsWith("vendor.contract", StringComparison.Ordinal))
                ? "Chọn hợp đồng cần xem để kiểm tra thời hạn và các chức năng gia hạn, chuyển địa điểm hoặc trả ô hiện có. Không suy ra hiệu lực giấy phép chỉ từ lịch sử này."
            : live.Any(e => e.Tool.StartsWith("ward.", StringComparison.Ordinal))
                ? "Mở màn hình nghiệp vụ để đối chiếu hồ sơ/chứng cứ gốc. Việc duyệt, từ chối, đình chỉ và xử phạt vẫn do cán bộ có thẩm quyền quyết định."
            : live.Any(e => e.Tool.StartsWith("admin.", StringComparison.Ordinal))
                ? "Mở mục tương ứng để xem xét trong phạm vi vận hành nền tảng. Quản trị nền tảng không thay cán bộ phường quyết định tuân thủ vỉa hè."
            : "Mở mục tương ứng để xem thông tin chi tiết và thực hiện bước tiếp theo.";
        return summary + (style == "detailed" ? "\n\n" + next : style == "steps" ? "\n\nBạn có thể theo checklist bên dưới để tiếp tục. Đây là hướng dẫn đối chiếu, chưa xác nhận việc nào đã hoàn tất." : "")
            + "\n\nDữ liệu phản ánh thời điểm tra cứu.";
    }
}
