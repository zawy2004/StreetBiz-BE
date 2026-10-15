using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Events;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;

namespace StreetBiz.Application.Features.Finance.GenerateFeeSchedule;

/// <summary>
/// SYS-03 for WARD-09: bill the days a renewal adds (<c>FirstDay</c> to <c>LastDay</c>, inclusive)
/// as new instalments on the contract's open schedule, at the zone's rent on the day the renewal is
/// approved. The instalments already on the schedule, paid or not, are left exactly as they are.
/// </summary>
public sealed record ExtendFeeScheduleCommand(long ContractId, DateOnly FirstDay, DateOnly LastDay, long ActorUserId)
    : IRequest<FeeScheduleDto>;

public sealed class ExtendFeeScheduleCommandValidator : AbstractValidator<ExtendFeeScheduleCommand>
{
    public ExtendFeeScheduleCommandValidator()
    {
        RuleFor(x => x.ContractId).GreaterThan(0);
        RuleFor(x => x.ActorUserId).GreaterThan(0);
        RuleFor(x => x.LastDay).GreaterThanOrEqualTo(x => x.FirstDay);
    }
}

public sealed class ExtendFeeScheduleCommandHandler(IFinanceRepository finance, TimeProvider clock)
    : IRequestHandler<ExtendFeeScheduleCommand, FeeScheduleDto>
{
    public async Task<FeeScheduleDto> Handle(ExtendFeeScheduleCommand request, CancellationToken cancellationToken)
    {
        var context = await finance.GetFeeScheduleContextAsync(request.ContractId, cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.ContractNotFound);
        if (context.ContractStatus != ContractStatuses.Active)
        {
            throw new DomainRuleException(FinanceMessages.ContractNotActive);
        }

        if (context.PricePerDay <= 0)
        {
            throw new DomainRuleException(FinanceMessages.ZoneMissingPrice);
        }

        var plan = FeeInstalmentPlanner.PlanExtension(
            context.PricePerDay, request.FirstDay, request.LastDay, BusinessCalendar.Today(clock));
        var schedule = await finance.AppendInstalmentsAsync(
            request.ContractId, request.ActorUserId, request.FirstDay, plan, cancellationToken);
        return schedule.ToDto(context.SlotCode);
    }
}

/// <summary>Bridges WARD-09 to SYS-03, as <see cref="GenerateFeeScheduleOnContractApproved"/> does for WARD-08.</summary>
public sealed class ExtendFeeScheduleOnContractRenewed(ISender sender)
    : INotificationHandler<RentalContractRenewedEvent>
{
    public Task Handle(RentalContractRenewedEvent notification, CancellationToken cancellationToken) =>
        sender.Send(
            new ExtendFeeScheduleCommand(
                notification.ContractId,
                notification.PreviousEndDate.AddDays(1),
                notification.NewEndDate,
                notification.ActorUserId),
            cancellationToken);
}
