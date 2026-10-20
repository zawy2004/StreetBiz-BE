using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Features.Finance.FeeReminders;

/// <summary>
/// FEE-02/SYS-06 entry point. Called by <c>FeeReminderHostedService</c> on a timer and by the
/// Development-only manual-trigger endpoint — both just publish this, neither knows the sweep's
/// logic. <c>Today</c> is a parameter rather than read from the clock inside the handler so a test
/// (or a manual replay of a specific day) can pass it explicitly.
/// </summary>
public sealed record RunFeeReminderSweepCommand(DateOnly Today) : IRequest<FeeReminderSweepResult>;

public sealed class RunFeeReminderSweepCommandHandler(IFinanceRepository finance)
    : IRequestHandler<RunFeeReminderSweepCommand, FeeReminderSweepResult>
{
    public Task<FeeReminderSweepResult> Handle(RunFeeReminderSweepCommand request, CancellationToken cancellationToken) =>
        finance.RunFeeReminderSweepAsync(request.Today, cancellationToken);
}
