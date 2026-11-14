namespace StreetBiz.Application.Common.Interfaces;

/// <summary>
/// A payment report StreetBiz could not apply as it stands, for a person to look at: money that
/// arrived with no matching payment, a wrong amount, a second transfer for something already
/// paid, a bad signature, or a server error while applying it.
/// </summary>
public sealed record PaymentAlert(
    string Provider,
    string Outcome,
    string Reason,
    string? ProviderReference,
    decimal? Amount,
    string? ProviderEventId,
    long? TransactionId,
    long? CallbackEventId);

/// <summary>
/// Tells the team about a payment that needs a person (a Discord channel in practice). Never
/// throws: a failed alert must not fail the callback that raised it.
/// </summary>
public interface IPaymentAlertNotifier
{
    Task NotifyAsync(PaymentAlert alert, CancellationToken cancellationToken);
}
