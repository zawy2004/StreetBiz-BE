namespace StreetBiz.Application.Common.Models;

/// <summary>A rental contract the vendor holds, with its current fee schedule's total.</summary>
public sealed record VendorContractRow(
    long ContractId,
    string SlotCode,
    string? ZoneName,
    string? WardName,
    string? Address,
    DateOnly StartDate,
    DateOnly EndDate,
    string ContractStatus,
    decimal ScheduleTotal);

/// <summary>One instalment of a contract's current fee schedule, with the invoice it produced if paid.</summary>
public sealed record ScheduleItemRow(
    long FeeItemId,
    long ContractId,
    int Ordinal,
    int OfCount,
    DateOnly DueDate,
    decimal Amount,
    string ItemStatus,
    DateTime? PaidAt,
    long? InvoiceId,
    string? InvoiceNumber);
