using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.VendorRegistration;

namespace StreetBiz.Application.Features.VendorRegistration.UpdateRegistration;

/// <summary>REG-04: edit an editable registration and re-submit it for review.</summary>
public sealed record UpdateRegistrationCommand(
    long RegistrationId,
    string VendorType,
    string DisplayName,
    string? DeclaredAddress,
    decimal? AddressLatitude,
    decimal? AddressLongitude,
    int WardUnitId) : IRequest<BusinessRegistrationDto>;

public sealed class UpdateRegistrationCommandValidator : AbstractValidator<UpdateRegistrationCommand>
{
    public UpdateRegistrationCommandValidator()
    {
        RuleFor(x => x.VendorType)
            .Must(t => VendorTypes.All.Contains(t))
            .WithMessage(RegMessages.SelectVendorType);
        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage(RegMessages.DisplayNameRequired)
            .MaximumLength(180).WithMessage(RegMessages.DisplayNameTooLong);
        RuleFor(x => x.DeclaredAddress)
            .NotEmpty()
            .When(x => x.VendorType == VendorTypes.FixedStorefront)
            .WithMessage(RegMessages.FixedNeedsAddress);
        RuleFor(x => x.WardUnitId).GreaterThan(0).WithMessage(AppMessages.InvalidWard);
    }
}

public sealed class UpdateRegistrationCommandHandler(
    IVendorContext vendorContext,
    IBusinessRegistrationRepository repository,
    IAdministrativeUnitRepository units) : IRequestHandler<UpdateRegistrationCommand, BusinessRegistrationDto>
{
    public async Task<BusinessRegistrationDto> Handle(UpdateRegistrationCommand request, CancellationToken cancellationToken)
    {
        var registration = await vendorContext.RequireOwnedRegistrationAsync(request.RegistrationId, cancellationToken);

        // BR-62: only DRAFT / SUBMITTED / MORE_INFORMATION_REQUIRED may be edited.
        if (!RegistrationStatuses.Editable.Contains(registration.RegistrationStatus))
        {
            throw new DomainRuleException(
                string.Format(RegMessages.NotEditable, RegMessages.StatusWord(registration.RegistrationStatus)));
        }

        await units.EnsureWardAsync(request.WardUnitId, cancellationToken);

        // BR-09: re-submitting (e.g. from MORE_INFORMATION_REQUIRED) must not leave the
        // vendor with a second application in review.
        if (await repository.HasActivePendingAsync(
                registration.VendorId, cancellationToken, excludeRegistrationId: registration.RegistrationId))
        {
            throw new ConflictException(RegMessages.DuplicatePending);
        }

        var data = new NewBizRegistration(
            request.VendorType, request.DisplayName, request.DeclaredAddress,
            request.AddressLatitude, request.AddressLongitude, request.WardUnitId);

        await repository.UpdateAndResubmitAsync(request.RegistrationId, data, cancellationToken);

        var updated = await repository.GetByIdAsync(request.RegistrationId, cancellationToken)
            ?? throw new NotFoundException(RegMessages.NotFound);
        return updated.ToDto();
    }
}
