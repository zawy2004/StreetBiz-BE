using Serilog.Events;
using Serilog.Parsing;
using StreetBiz.API.Extensions;

namespace StreetBiz.API.Tests;

/// <summary>Only EF Core's entries for cancelled calls are dropped; real database errors stay.</summary>
public sealed class LogNoiseFilterTests
{
    private static LogEvent Event(string source, Exception? exception) => new(
        DateTimeOffset.UtcNow, LogEventLevel.Error, exception,
        new MessageTemplateParser().Parse("An error occurred using the connection to database"),
        [new LogEventProperty("SourceContext", new ScalarValue(source))]);

    [Fact]
    public void A_cancelled_ef_core_call_is_dropped() =>
        Assert.True(LogNoiseFilter.IsCancelledDatabaseCall(
            Event("Microsoft.EntityFrameworkCore.Database.Connection", new OperationCanceledException())));

    [Fact]
    public void A_cancelled_ef_core_command_is_dropped_too() =>
        Assert.True(LogNoiseFilter.IsCancelledDatabaseCall(
            Event("Microsoft.EntityFrameworkCore.Database.Command", new TaskCanceledException())));

    [Fact]
    public void A_real_database_error_is_kept() =>
        Assert.False(LogNoiseFilter.IsCancelledDatabaseCall(
            Event("Microsoft.EntityFrameworkCore.Database.Connection", new InvalidOperationException("Login failed"))));

    [Fact]
    public void A_cancellation_logged_by_anything_else_is_kept()
    {
        // An HttpClient timeout to Gemini or payOS is a TaskCanceledException and must stay visible.
        Assert.False(LogNoiseFilter.IsCancelledDatabaseCall(
            Event("System.Net.Http.HttpClient.Gemini.LogicalHandler", new TaskCanceledException())));
        Assert.False(LogNoiseFilter.IsCancelledDatabaseCall(
            Event("StreetBiz.API.Extensions.GlobalExceptionHandler", new OperationCanceledException())));
    }

    private static LogEvent EfEvent(string source, int eventId, string eventName) => new(
        DateTimeOffset.UtcNow, LogEventLevel.Error, null,
        new MessageTemplateParser().Parse("An error occurred using the connection to database"),
        [
            new LogEventProperty("SourceContext", new ScalarValue(source)),
            new LogEventProperty("EventId", new StructureValue(
                [new LogEventProperty("Id", new ScalarValue(eventId)), new LogEventProperty("Name", new ScalarValue(eventName))])),
        ]);

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Connection", 20004, "ConnectionError")]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Transaction", 20205, "TransactionError")]
    public void Ef_core_connection_and_transaction_errors_logged_without_an_exception_are_dropped(
        string source, int eventId, string name) =>
        // What EF writes for a request the client abandoned (reproduced with aborted requests).
        Assert.True(LogNoiseFilter.IsCancelledDatabaseCall(EfEvent(source, eventId, name)));

    [Fact]
    public void Other_ef_core_entries_without_an_exception_are_kept() =>
        Assert.False(LogNoiseFilter.IsCancelledDatabaseCall(
            EfEvent("Microsoft.EntityFrameworkCore.Database.Command", 20102, "CommandError")));

    [Fact]
    public void An_entry_without_an_exception_or_event_id_is_kept() =>
        Assert.False(LogNoiseFilter.IsCancelledDatabaseCall(
            Event("Microsoft.EntityFrameworkCore.Database.Connection", null)));
}
