using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Infrastructure.Persistence;

/// <summary>
/// Translates SQL Server errors raised by triggers and constraints on the sidewalk/rental
/// tables into the typed <see cref="AppException"/>s the API layer already knows how to
/// render, instead of letting them surface as an unhandled 500.
///
/// TR_RentalContracts_NoOverlap and TR_RentalContracts_NoCancelWithDebt both RAISERROR(...,16,1)
/// as error 50000, so they must be told apart by message text, not error number. Both also
/// ROLLBACK TRANSACTION inside the trigger, which raises an additional 3609 ("The transaction
/// ended in the trigger. Batch has been aborted.") — SqlException.Errors is a collection and
/// 3609 is not necessarily the last one, so every error in it is inspected.
/// </summary>
public static class SqlErrorTranslator
{
    /// <summary>
    /// Repositories call this from a catch around SaveChangesAsync. Returns null when the
    /// exception is not one this translator recognizes, so the caller can rethrow the original.
    /// </summary>
    public static AppException? TryTranslate(DbUpdateException exception)
    {
        if (exception.GetBaseException() is not SqlException sqlException)
        {
            return null;
        }

        foreach (SqlError error in sqlException.Errors)
        {
            var translated = Translate(error.Number, error.Message);
            if (translated is not null)
            {
                return translated;
            }
        }

        return null;
    }

    /// <summary>
    /// Testable core. SqlException/SqlError have no public constructor, so unit tests exercise
    /// this overload directly with the number/message they want to simulate.
    /// </summary>
    public static AppException? Translate(int number, string message)
    {
        if (number == 50000)
        {
            return TranslateRaisError(message);
        }

        if (number is 2601 or 2627)
        {
            return TranslateUniqueViolation(message);
        }

        if (number == 547)
        {
            return new ValidationAppException(new Dictionary<string, string[]>
            {
                ["Reference"] = ["The request refers to a record that does not exist or is not allowed here."],
            });
        }

        return null;
    }

    private static AppException? TranslateRaisError(string message)
    {
        if (message.Contains("overlapping this period", StringComparison.OrdinalIgnoreCase))
        {
            return new ConflictException(SideMessages.SlotAlreadyBooked);
        }

        if (message.Contains("fees are overdue or penalties unpaid", StringComparison.OrdinalIgnoreCase))
        {
            return new DomainRuleException(SideMessages.CannotReturnWithDebt);
        }

        return null;
    }

    private static AppException TranslateUniqueViolation(string message)
    {
        // Filtered unique indexes carry an explicit, stable name — match on it directly.
        if (message.Contains("UQ_RenewalRequests_OpenPerContract", StringComparison.OrdinalIgnoreCase))
        {
            return new ConflictException(SideMessages.RenewalAlreadyOpen);
        }

        if (message.Contains("UQ_AddressChangeRequests_OpenPerRegistration", StringComparison.OrdinalIgnoreCase))
        {
            return new ConflictException(SideMessages.AddressChangeAlreadyOpen);
        }

        if (message.Contains("UQ_DigitalPermits_LivePerContract", StringComparison.OrdinalIgnoreCase))
        {
            return new ConflictException("This contract already has a live digital permit.");
        }

        // slot_code and qr_payload are plain inline UNIQUE columns, so SQL Server auto-names
        // their constraint (e.g. "UQ__SidewalkSlots__<hash>") and that hash is not guaranteed
        // stable across environments. Fall back to the table name, which the message always
        // carries as "object 'dbo.<Table>'".
        if (message.Contains("SidewalkSlots", StringComparison.OrdinalIgnoreCase))
        {
            return new ConflictException(SideMessages.SlotCodeGenerationFailed);
        }

        if (message.Contains("DigitalPermits", StringComparison.OrdinalIgnoreCase))
        {
            return new ConflictException("Generated permit token collided; please retry.");
        }

        return new ConflictException("A conflicting record already exists.");
    }
}
