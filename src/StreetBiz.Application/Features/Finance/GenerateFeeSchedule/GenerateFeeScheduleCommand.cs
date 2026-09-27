using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;
using StreetBiz.Application.Features.SidewalkSlots.GetSlotQuote;

namespace StreetBiz.Application.Features.Finance.GenerateFeeSchedule;

/// <summary>
/// SYS-03: generate (or regenerate) the fee schedule of a rental contract. Raised by WARD-08 on
/// approval and by WARD-09 on renewal through <see cref="RentalContractApprovedEvent"/>; exposed
/// directly only through the Development-only endpoint used while WARD-08 is still being built.
/// </summary>
public sealed record GenerateFeeScheduleCommand(long ContractId, long ActorUserId)
    : IRequest<FeeScheduleDto>;

public sealed class GenerateFeeScheduleCommandValidator : AbstractValidator<GenerateFeeScheduleCommand>
{
    public GenerateFeeScheduleCommandValidator()
    {
        RuleFor(x => x.ContractId).GreaterThan(0);
        RuleFor(x => x.ActorUserId).GreaterThan(0);
    }
}

public sealed class GenerateFeeScheduleCommandHandler(IFinanceRepository finance)
    : IRequestHandler<GenerateFeeScheduleCommand, FeeScheduleDto>
{
    public async Task<FeeScheduleDto> Handle(
        GenerateFeeScheduleCommand request,
        CancellationToken cancellationToken)
    {
        var context = await finance.GetFeeScheduleContextAsync(request.ContractId, cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.ContractNotFound);

        if (context.PricePerDay <= 0)
        {
            throw new DomainRuleException(FinanceMessages.ZoneMissingPrice);
        }

        // Same calculator as the SIDE-02 quote, so the schedule cannot drift from the price the
        // vendor applied against.
        var quote = FeeQuoteCalculator.Calculate(
            context.SlotId, context.PricePerDay, context.TermDays, context.Components);

        var plan = FeeInstalmentPlanner.Plan(quote, context.StartDate);

        var schedule = await finance.ReplaceFeeScheduleAsync(
            context.ContractId, request.ActorUserId, plan.Total, plan.Instalments, cancellationToken);

        return schedule.ToDto(context.SlotCode);
    }
}
