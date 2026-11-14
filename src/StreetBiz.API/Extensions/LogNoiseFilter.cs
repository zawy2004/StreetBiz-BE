using Serilog.Events;

namespace StreetBiz.API.Extensions;

/// <summary>
/// Drops EF Core's own error entries for database calls that were cancelled because the client
/// left (tab closed, navigated on, lost signal). EF logs those at Error ("An error occurred using
/// the connection to database", "Failed executing DbCommand") although nothing failed; the request
/// itself is still logged once, as a 499, by <see cref="GlobalExceptionHandler"/>.
/// <para>
/// Only cancellations are dropped: a broken connection, a timeout or a bad query carries a
/// different exception and is logged exactly as before.
/// </para>
/// </summary>
public static class LogNoiseFilter
{
    /// <summary>EF Core's ConnectionError and TransactionError events.</summary>
    private static readonly HashSet<int> ConnectionAndTransactionErrors = [20004, 20205];

    public static bool IsCancelledDatabaseCall(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("SourceContext", out var source)
            || source is not ScalarValue { Value: string context }
            || !context.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
        {
            return false;
        }

        if (logEvent.Exception is { } exception)
        {
            return IsCancellation(exception);
        }

        // On a cancelled request EF logs these two at Error with no exception attached. A real
        // connection or transaction failure always fails its request too, and GlobalExceptionHandler
        // logs that one at Error with the full exception, so nothing is lost by dropping them here.
        return logEvent.Properties.TryGetValue("EventId", out var eventId)
            && eventId is StructureValue structure
            && structure.Properties.FirstOrDefault(p => p.Name == "Id")?.Value is ScalarValue { Value: int id }
            && ConnectionAndTransactionErrors.Contains(id);
    }

    private static bool IsCancellation(Exception exception) =>
        exception is OperationCanceledException
        // SqlClient reports a cancelled command as a SqlException, not an OperationCanceledException.
        || (exception.GetType().Name == "SqlException"
            && exception.Message.Contains("Operation cancelled by user", StringComparison.OrdinalIgnoreCase));
}
