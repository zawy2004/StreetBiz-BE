using MediatR;

namespace StreetBiz.Application.Common.Events;

/// <summary>
/// Raised when a rental contract starts or changes term: WARD-08 approving an application
/// (BR-17) and WARD-09 approving a renewal. The finance module subscribes to generate the
/// contract's fee schedule (SYS-03, BR-18).
///
/// It is a notification, not a command, so the ward workflow stays unaware of the finance
/// module: publish it and any number of subscribers can react.
///
/// Lives in Application rather than Domain because this codebase is database-first — the Domain
/// project holds no entities to raise events from, and adding MediatR to it would give a
/// deliberately framework-free project a framework dependency.
/// </summary>
/// <param name="ContractId">The contract whose schedule must be (re)generated.</param>
/// <param name="ActorUserId">The ward officer whose decision caused this, recorded in the audit log.</param>
/// <param name="Reason">Short reason for the audit trail, e.g. "WARD-08 approval" or "WARD-09 renewal".</param>
public sealed record RentalContractApprovedEvent(
    long ContractId,
    long ActorUserId,
    string Reason) : INotification;
