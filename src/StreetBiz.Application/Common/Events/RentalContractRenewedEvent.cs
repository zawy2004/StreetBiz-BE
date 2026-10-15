using MediatR;

namespace StreetBiz.Application.Common.Events;

/// <summary>
/// Raised when WARD-09 approves a renewal and moves the contract's end date. The finance module
/// subscribes to bill the added days (SYS-03): they are appended to the contract's open schedule,
/// which keeps every instalment of the current term, paid or not.
///
/// Publish it inside the same transaction as the renewal write, in place of superseding the
/// schedule: a schedule the vendor has paid into cannot be regenerated, and superseding it would
/// drop the instalments of the current term that are not due yet.
/// </summary>
/// <param name="ContractId">The renewed contract.</param>
/// <param name="PreviousEndDate">The last day of the term before the renewal.</param>
/// <param name="NewEndDate">The last day of the renewed term.</param>
/// <param name="ActorUserId">The ward officer who approved the renewal, recorded in the audit log.</param>
public sealed record RentalContractRenewedEvent(
    long ContractId,
    DateOnly PreviousEndDate,
    DateOnly NewEndDate,
    long ActorUserId) : INotification;
