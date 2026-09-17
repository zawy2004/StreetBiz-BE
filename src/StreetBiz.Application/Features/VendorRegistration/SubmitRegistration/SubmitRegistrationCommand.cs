using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.VendorRegistration;

namespace StreetBiz.Application.Features.VendorRegistration.SubmitRegistration;

/// <summary>REG-01: submit a business-registration application.</summary>
public sealed record SubmitRegistrationCommand(
    string VendorType,
    string DisplayName,
    string? DeclaredAddress,
    decimal? AddressLatitude,
    decimal? AddressLongitude,
    int WardUnitId) : IRequest<BusinessRegistrationDto>;

public sealed class SubmitRegistrationCommandValidator : AbstractValidator<SubmitRegistrationCommand>
{
    public SubmitRegistrationCommandValidator()
    {
        RuleFor(x => x.VendorType)
            .Must(t => VendorTypes.All.Contains(t))
            .WithMessage(RegMessages.SelectVendorType);

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("Business/display name is required.")
            .MaximumLength(180);

        // BR-07: a fixed storefront must declare an address.
        RuleFor(x => x.DeclaredAddress)
            .NotEmpty()
            .When(x => x.VendorType == VendorTypes.FixedStorefront)
            .WithMessage(RegMessages.FixedNeedsAddress);

        RuleFor(x => x.WardUnitId).GreaterThan(0);
    }
}

public sealed class SubmitRegistrationCommandHandler(
    IVendorContext vendorContext,
    IBusinessRegistrationRepository repository,
    IAdministrativeUnitRepository units) : IRequestHandler<SubmitRegistrationCommand, BusinessRegistrationDto>
{
    public async Task<BusinessRegistrationDto> Handle(SubmitRegistrationCommand request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        await units.EnsureWardAsync(request.WardUnitId, cancellationToken);

        // BR-09: at most one active (SUBMITTED/UNDER_REVIEW) registration per vendor.
        if (await repository.HasActivePendingAsync(vendorId, cancellationToken))
        {
            throw new ConflictException(RegMessages.DuplicatePending);
        }

        var data = new NewBizRegistration(
            request.VendorType, request.DisplayName, request.DeclaredAddress,
            request.AddressLatitude, request.AddressLongitude, request.WardUnitId);

        var id = await repository.CreateAsync(vendorId, data, cancellationToken);

        var created = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException(RegMessages.NotFound);

        return created.ToDto();
    }
}
