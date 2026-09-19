using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.RentalApplications.WithdrawApplication;

/// <summary>SIDE-04: withdraw a rental application that has not yet been decided.</summary>
public sealed record WithdrawApplicationCommand(long ApplicationId) : IRequest<Unit>;

public sealed class WithdrawApplicationCommandHandler(
    IVendorContext vendorContext,
    IRentalApplicationRepository applications)
    : IRequestHandler<WithdrawApplicationCommand, Unit>
{
    public async Task<Unit> Handle(WithdrawApplicationCommand request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);

        var application = await applications.GetByIdAsync(request.ApplicationId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.ApplicationNotFound);

        if (application.VendorId != vendorId)
        {
            throw new ForbiddenException(AppMessages.Forbidden);
        }

        if (!ApplicationStatuses.Withdrawable.Contains(application.ApplicationStatus))
        {
            throw new ConflictException(SideMessages.ApplicationNotWithdrawable);
        }

        await applications.SetStatusAsync(request.ApplicationId, ApplicationStatuses.Withdrawn, cancellationToken);
        return Unit.Value;
    }
}
