using System.Collections.Concurrent;

namespace StreetBiz.Application.Features.Chatbot;

/// <summary>Single-node load shedding/cancellation. DB leases provide the conversation invariant.</summary>
public sealed class ChatbotRuntime(ChatbotSettings settings) : IDisposable
{
    private readonly SemaphoreSlim global = new(settings.GlobalConcurrentTurns);
    private readonly Dictionary<long, int> accounts = new();
    private readonly object accountGate = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> turns = new();

    public IDisposable Enter(long userId)
    {
        if (!global.Wait(0)) throw new ChatbotException(429, "busy", "Trợ lý đang bận. Vui lòng thử lại sau ít phút.");
        lock (accountGate)
        {
            var count = accounts.GetValueOrDefault(userId);
            if (count >= 2) { global.Release(); throw new ChatbotException(429, "concurrency_limit", "Bạn đang có nhiều câu hỏi được xử lý. Hãy chờ một chút."); }
            accounts[userId] = count + 1;
        }
        return new Release(() => {
            lock (accountGate) { if (accounts[userId] == 1) accounts.Remove(userId); else accounts[userId]--; }
            global.Release();
        });
    }

    public IDisposable Register(string id, CancellationTokenSource source)
    {
        turns[id] = source;
        return new Release(() => turns.TryRemove(id, out _));
    }

    public void Cancel(string id)
    {
        if (turns.TryGetValue(id, out var token))
            try { token.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose() => global.Dispose();
    private sealed class Release(Action action) : IDisposable
    {
        private Action? dispose = action;
        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}
