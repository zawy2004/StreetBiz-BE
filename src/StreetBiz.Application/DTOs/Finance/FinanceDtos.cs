using System.Globalization;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.DTOs.Finance;

/// <summary>One fee instalment as the vendor sees it (FEE-01).</summary>
public sealed record FeeItemDto(
    long FeeItemId,
    long ContractId,
    string SlotCode,
    string PeriodLabel,
    DateOnly DueDate,
    decimal Amount,
    string ItemStatus,
    DateTime? PaidAt);

/// <summary>A fee schedule revision and its instalments (SYS-03).</summary>
public sealed record FeeScheduleDto(
    long FeeScheduleId,
    long ContractId,
    string SlotCode,
    int Revision,
    decimal TotalAmount,
    DateTime GeneratedAt,
    IReadOnlyList<FeeItemDto> Items);

/// <summary>A penalty as the vendor sees it (FEE-04).</summary>
public sealed record PenaltyDto(
    long PenaltyId,
    string ViolationLabel,
    string? SlotCode,
    decimal Amount,
    string PenaltyStatus);

/// <summary>One penalty in FinanceHome's "Biên bản phạt" tab.</summary>
public sealed record PenaltyListDto(
    long PenaltyId,
    long ViolationId,
    string ViolationType,
    string ViolationLabel,
    string? SlotCode,
    decimal Amount,
    string PenaltyStatus,
    DateTime IssuedAt,
    DateTime? PaidAt);

/// <summary>FinanceHome's top summary card.</summary>
public sealed record FinanceSummaryDto(
    decimal FeeDue,
    decimal PenaltyDue,
    decimal TotalDue,
    int OverdueCount,
    DateOnly? NextDueDate);

/// <summary>FEE-05: one payment attempt.</summary>
public sealed record PaymentTransactionDto(
    long TransactionId,
    string Purpose,
    string Provider,
    decimal Amount,
    string TransactionStatus,
    string ReferenceLabel,
    string? SlotCode,
    DateTime CreatedAt,
    DateTime? CallbackReceivedAt);

/// <summary>FEE-05: one violation recorded against the vendor.</summary>
public sealed record VendorViolationDto(
    long ViolationId,
    string ViolationType,
    string ViolationLabel,
    string? Description,
    string? EvidenceUrl,
    string Source,
    DateTime RecordedAt,
    string? SlotCode,
    decimal? PenaltyAmount,
    string? PenaltyStatus);

/// <summary>One invoice in a list (FEE-03).</summary>
public sealed record InvoiceDto(
    long InvoiceId,
    string InvoiceNumber,
    string Kind,
    decimal Amount,
    DateTime IssuedAt,
    string? PeriodLabel);

/// <summary>Full invoice detail (FEE-03).</summary>
public sealed record InvoiceDetailDto(
    long InvoiceId,
    string InvoiceNumber,
    string Kind,
    decimal Amount,
    DateTime IssuedAt,
    string? PeriodLabel,
    string? SlotCode,
    string? ViolationLabel,
    long? FeeItemId,
    long? PenaltyId,
    string? PaymentProvider,
    DateTime? PaidAt);

/// <summary>FEE-01/FEE-04: a checkout was opened, here is where to send the vendor to pay it.</summary>
public sealed record FinanceCheckoutDto(
    long TransactionId,
    string Purpose,
    long ReferenceId,
    string Provider,
    decimal Amount,
    string PaymentUrl);

public static class FinanceMapper
{
    /// <summary>
    /// FeeScheduleItems stores no label: the period is the instalment's position in its schedule
    /// plus the month its payment falls due, which is what the vendor's finance screen shows.
    /// </summary>
    public static string PeriodLabel(int ordinal, int ofCount, DateOnly dueDate) =>
        ofCount <= 1
            ? $"Kỳ duy nhất · Tháng {dueDate.ToString("MM/yyyy", CultureInfo.InvariantCulture)}"
            : $"Kỳ {ordinal}/{ofCount} · Tháng {dueDate.ToString("MM/yyyy", CultureInfo.InvariantCulture)}";

    public static FeeItemDto ToDto(this FeeItemRow row, long contractId, string slotCode) => new(
        row.FeeItemId,
        contractId,
        slotCode,
        PeriodLabel(row.Ordinal, row.OfCount, row.DueDate),
        row.DueDate,
        row.Amount,
        row.ItemStatus,
        // SQL Server returns DATETIME2 as Unspecified; the column is written in UTC, so say so
        // rather than letting the client read it as local wall-clock time.
        row.PaidAt is null ? null : DateTime.SpecifyKind(row.PaidAt.Value, DateTimeKind.Utc));

    public static FeeScheduleDto ToDto(this FeeScheduleRow row, string slotCode) => new(
        row.FeeScheduleId,
        row.ContractId,
        slotCode,
        row.Revision,
        row.TotalAmount,
        DateTime.SpecifyKind(row.GeneratedAt, DateTimeKind.Utc),
        row.Items.Select(item => item.ToDto(row.ContractId, slotCode)).ToList());

    public static string FeeItemPeriodLabel(this FeeItemCheckoutRow row) =>
        PeriodLabel(row.Ordinal, row.OfCount, row.DueDate);

    public static PenaltyDto ToDto(this PenaltyCheckoutRow row) => new(
        row.PenaltyId, row.ViolationLabel, row.SlotCode, row.Amount, row.PenaltyStatus);

    public static InvoiceDto ToListDto(this InvoiceRow row) => new(
        row.InvoiceId, row.InvoiceNumber, row.Kind, row.Amount, row.IssuedAt, row.PeriodLabel);

    public static InvoiceDetailDto ToDetailDto(this InvoiceRow row) => new(
        row.InvoiceId, row.InvoiceNumber, row.Kind, row.Amount, row.IssuedAt, row.PeriodLabel,
        row.SlotCode, row.ViolationLabel, row.FeeItemId, row.PenaltyId, row.PaymentProvider, row.PaidAt);

    public static FeeItemDto ToDto(this FeeItemListRow row) => new(
        row.FeeItemId,
        row.ContractId,
        row.SlotCode,
        PeriodLabel(row.Ordinal, row.OfCount, row.DueDate),
        row.DueDate,
        row.Amount,
        row.ItemStatus,
        row.PaidAt is null ? null : DateTime.SpecifyKind(row.PaidAt.Value, DateTimeKind.Utc));

    public static PenaltyListDto ToDto(this PenaltyListRow row) => new(
        row.PenaltyId, row.ViolationId, row.ViolationType, row.ViolationLabel, row.SlotCode,
        row.Amount, row.PenaltyStatus,
        DateTime.SpecifyKind(row.IssuedAt, DateTimeKind.Utc),
        row.PaidAt is null ? null : DateTime.SpecifyKind(row.PaidAt.Value, DateTimeKind.Utc));

    public static FinanceSummaryDto ToDto(this FinanceSummaryRow row) => new(
        row.FeeDue, row.PenaltyDue, row.FeeDue + row.PenaltyDue, row.OverdueCount, row.NextDueDate);

    public static PaymentTransactionDto ToDto(this PaymentTransactionRow row) => new(
        row.TransactionId, row.Purpose, row.Provider, row.Amount, row.TransactionStatus,
        row.ReferenceLabel, row.SlotCode,
        DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
        row.CallbackReceivedAt is null ? null : DateTime.SpecifyKind(row.CallbackReceivedAt.Value, DateTimeKind.Utc));

    public static VendorViolationDto ToDto(this VendorViolationRow row) => new(
        row.ViolationId, row.ViolationType, row.ViolationLabel, row.Description, row.EvidenceUrl,
        row.Source, DateTime.SpecifyKind(row.RecordedAt, DateTimeKind.Utc), row.SlotCode,
        row.PenaltyAmount, row.PenaltyStatus);
}
