using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;

namespace StreetBiz.Application.Features.Finance.VendorContracts;

/// <summary>
/// How far a contract's fee schedule has been paid. Pure: the screens' progress bars, overdue
/// counts and "next instalment" all come from here, so they cannot disagree with each other.
///
/// "Overdue" is judged by the due date, not only by the OVERDUE status: the reminder sweep flips
/// statuses hourly, and an instalment due yesterday must not look on time in the gap.
/// </summary>
public static class FeeScheduleProgress
{
    public static bool IsOutstanding(string status) =>
        status is FeeItemStatuses.Pending or FeeItemStatuses.Overdue;

    public static ScheduleItemDto ToDto(ScheduleItemRow row, DateOnly today)
    {
        var outstanding = IsOutstanding(row.ItemStatus);
        var late = outstanding && row.DueDate < today;
        return new ScheduleItemDto(
            row.FeeItemId,
            row.Ordinal,
            row.OfCount,
            FinanceMapper.PeriodLabel(row.Ordinal, row.OfCount, row.DueDate),
            row.DueDate,
            row.Amount,
            // The sweep may not have run yet today; the screen should already say "quá hạn".
            late ? FeeItemStatuses.Overdue : row.ItemStatus,
            row.PaidAt is null ? null : DateTime.SpecifyKind(row.PaidAt.Value, DateTimeKind.Utc),
            row.InvoiceId,
            row.InvoiceNumber,
            late ? today.DayNumber - row.DueDate.DayNumber : null,
            outstanding && !late ? row.DueDate.DayNumber - today.DayNumber : null);
    }

    public static VendorContractFinanceDto Summarize(
        VendorContractRow contract, IReadOnlyList<ScheduleItemRow> items, DateOnly today)
    {
        var dtos = items.OrderBy(item => item.DueDate).Select(item => ToDto(item, today)).ToList();
        var paid = dtos.Where(item => item.ItemStatus == FeeItemStatuses.Paid).ToList();
        var outstanding = dtos.Where(item => IsOutstanding(item.ItemStatus)).ToList();
        return new VendorContractFinanceDto(
            contract.ContractId,
            contract.SlotCode,
            contract.ZoneName,
            contract.WardName,
            contract.Address,
            contract.StartDate,
            contract.EndDate,
            contract.ContractStatus,
            // The planner makes the instalments sum to the schedule total; summing them keeps the
            // three figures consistent even if a total were ever edited by hand.
            dtos.Sum(item => item.Amount),
            paid.Sum(item => item.Amount),
            outstanding.Sum(item => item.Amount),
            dtos.Count,
            paid.Count,
            outstanding.Count(item => item.DaysOverdue is not null),
            outstanding.FirstOrDefault());
    }
}

/// <summary>FinanceHome: every contract with a fee schedule, the one in use first.</summary>
public sealed record ListVendorContractsQuery : IRequest<IReadOnlyList<VendorContractFinanceDto>>;

public sealed class ListVendorContractsQueryHandler(
    IVendorContext vendorContext,
    IVendorFinanceRepository finance,
    TimeProvider clock) : IRequestHandler<ListVendorContractsQuery, IReadOnlyList<VendorContractFinanceDto>>
{
    public async Task<IReadOnlyList<VendorContractFinanceDto>> Handle(
        ListVendorContractsQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var contracts = await finance.ListContractsAsync(vendorId, cancellationToken);
        var items = await finance.ListScheduleItemsAsync(vendorId, null, cancellationToken);
        var today = BusinessCalendar.Today(clock);
        var byContract = items.ToLookup(item => item.ContractId);

        return contracts
            .Select(contract => FeeScheduleProgress.Summarize(contract, byContract[contract.ContractId].ToList(), today))
            .OrderByDescending(contract => contract.ContractStatus == ContractStatuses.Active)
            .ThenByDescending(contract => contract.OverdueCount)
            .ThenByDescending(contract => contract.StartDate)
            .ToList();
    }
}

/// <summary>One contract's schedule, instalment by instalment.</summary>
public sealed record GetContractScheduleQuery(long ContractId) : IRequest<ContractScheduleDto>;

public sealed class GetContractScheduleQueryHandler(
    IVendorContext vendorContext,
    IVendorFinanceRepository finance,
    TimeProvider clock) : IRequestHandler<GetContractScheduleQuery, ContractScheduleDto>
{
    public async Task<ContractScheduleDto> Handle(GetContractScheduleQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var contract = (await finance.ListContractsAsync(vendorId, cancellationToken))
            .SingleOrDefault(row => row.ContractId == request.ContractId)
            ?? throw new NotFoundException(FinanceMessages.ContractNotFound);
        var items = await finance.ListScheduleItemsAsync(vendorId, request.ContractId, cancellationToken);
        var today = BusinessCalendar.Today(clock);

        return new ContractScheduleDto(
            FeeScheduleProgress.Summarize(contract, items, today),
            items.OrderBy(item => item.DueDate).Select(item => FeeScheduleProgress.ToDto(item, today)).ToList());
    }
}

/// <summary>One instalment and its contract: what the payment screen shows, before and after paying.</summary>
public sealed record GetFeeItemDetailQuery(long FeeItemId) : IRequest<FeeItemDetailDto>;

public sealed class GetFeeItemDetailQueryHandler(
    IVendorContext vendorContext,
    IVendorFinanceRepository finance,
    TimeProvider clock) : IRequestHandler<GetFeeItemDetailQuery, FeeItemDetailDto>
{
    public async Task<FeeItemDetailDto> Handle(GetFeeItemDetailQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var items = await finance.ListScheduleItemsAsync(vendorId, null, cancellationToken);
        var item = items.SingleOrDefault(row => row.FeeItemId == request.FeeItemId)
            ?? throw new NotFoundException(FinanceMessages.FeeItemNotFound);
        var contract = (await finance.ListContractsAsync(vendorId, cancellationToken))
            .Single(row => row.ContractId == item.ContractId);
        var today = BusinessCalendar.Today(clock);

        return new FeeItemDetailDto(
            FeeScheduleProgress.Summarize(contract, items.Where(row => row.ContractId == item.ContractId).ToList(), today),
            FeeScheduleProgress.ToDto(item, today));
    }
}
