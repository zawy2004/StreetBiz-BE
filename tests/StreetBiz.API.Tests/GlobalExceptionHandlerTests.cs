using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StreetBiz.API.Extensions;

namespace StreetBiz.API.Tests;

/// <summary>
/// A request the client abandoned (tab closed, navigated on) is not a server fault: no error log,
/// no 500. A real fault while the client is still waiting stays an error.
/// </summary>
public sealed class GlobalExceptionHandlerTests
{
    private sealed class CapturingLog : ILogger<GlobalExceptionHandler>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }

    private static (DefaultHttpContext Context, CancellationTokenSource Abort) Request()
    {
        var abort = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = abort.Token };
        context.Request.Method = "GET";
        context.Request.Path = "/api/chat/conversations";
        context.Response.Body = new MemoryStream();
        return (context, abort);
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(InvalidOperationException))] // e.g. SqlException "Operation cancelled by user"
    public async Task A_request_the_client_abandoned_is_499_and_not_logged_as_an_error(Type exceptionType)
    {
        var log = new CapturingLog();
        var (context, abort) = Request();
        abort.Cancel();

        var handled = await new GlobalExceptionHandler(log)
            .TryHandleAsync(context, (Exception)Activator.CreateInstance(exceptionType)!, default);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.DoesNotContain(LogLevel.Error, log.Levels);
    }

    [Fact]
    public async Task A_fault_while_the_client_is_still_waiting_stays_a_logged_500()
    {
        var log = new CapturingLog();
        var (context, _) = Request();

        await new GlobalExceptionHandler(log).TryHandleAsync(context, new InvalidOperationException("boom"), default);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Contains(LogLevel.Error, log.Levels);
    }
}
