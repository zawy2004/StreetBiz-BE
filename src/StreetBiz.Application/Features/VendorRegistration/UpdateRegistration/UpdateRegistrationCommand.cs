using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.VendorRegistration;

namespace StreetBiz.Application.Features.VendorRegistration.UpdateRegistration;

/// <summary>REG-04: edit an editable registration and re-submit it for review. Field set mirrors
/// SubmitRegistrationCommand -- see its remarks for why (Mẫu số 01 Phụ lục II, Thông tư
/// 68/2025/TT-BTC).</summary>
public sealed record UpdateRegistrationCommand(
    long RegistrationId,
    string VendorType,
    string DisplayName,
    string? DeclaredAddress,
    decimal? AddressLatitude,
    decimal? AddressLongitude,
    int WardUnitId,
    DateOnly? OwnerDateOfBirth = null,
    string? OwnerGender = null,
    string? OwnerEthnicity = null,
    string? OwnerNationality = null,
    string? IdType = null,
    DateOnly? IdIssuedDate = null,
    string? IdIssuedPlace = null,
    string? PermanentAddress = null,
    string? ContactAddress = null,
    string? BusinessLine = null,
    string? BusinessLineCode = null,
    decimal? CapitalAmount = null,
    int? LaborCount = null,
    DateOnly? PlannedStartDate = null,
    bool FoodSafetyCommitment = false,
    IReadOnlyList<NewHouseholdMember>? HouseholdMembers = null) : IRequest<BusinessRegistrationDto>;

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

        RuleFor(x => x.OwnerDateOfBirth).NotNull().WithMessage(RegMessages.OwnerDateOfBirthRequired);
        RuleFor(x => x.OwnerGender)
            .Must(g => g != null && OwnerGenders.All.Contains(g))
            .WithMessage(RegMessages.OwnerGenderRequired);
        RuleFor(x => x.OwnerNationality).NotEmpty().WithMessage(RegMessages.OwnerNationalityRequired);
        RuleFor(x => x.IdType)
            .Must(t => t != null && OwnerIdTypes.All.Contains(t))
            .WithMessage(RegMessages.IdTypeRequired);
        RuleFor(x => x.IdIssuedDate).NotNull().WithMessage(RegMessages.IdIssuedDateRequired);
        RuleFor(x => x.IdIssuedPlace).NotEmpty().WithMessage(RegMessages.IdIssuedPlaceRequired);
        RuleFor(x => x.PermanentAddress).NotEmpty().WithMessage(RegMessages.PermanentAddressRequired);

        RuleFor(x => x.BusinessLine).NotEmpty().WithMessage(RegMessages.BusinessLineRequired);
        RuleFor(x => x.CapitalAmount).NotNull().GreaterThanOrEqualTo(0)
            .WithMessage(RegMessages.CapitalAmountRequired);
        RuleFor(x => x.LaborCount).NotNull().GreaterThanOrEqualTo(0)
            .WithMessage(RegMessages.LaborCountRequired);
        RuleFor(x => x.PlannedStartDate).NotNull().WithMessage(RegMessages.PlannedStartDateRequired);

        RuleForEach(x => x.HouseholdMembers).ChildRules(member =>
        {
            member.RuleFor(m => m.FullName).NotEmpty().WithMessage(RegMessages.HouseholdMemberNameRequired);
        });
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
            request.AddressLatitude, request.AddressLongitude, request.WardUnitId,
            request.OwnerDateOfBirth, request.OwnerGender, request.OwnerEthnicity, request.OwnerNationality,
            request.IdType, request.IdIssuedDate, request.IdIssuedPlace, request.PermanentAddress, request.ContactAddress,
            request.BusinessLine, request.BusinessLineCode, request.CapitalAmount, request.LaborCount, request.PlannedStartDate,
            request.FoodSafetyCommitment, request.HouseholdMembers);

        await repository.UpdateAndResubmitAsync(request.RegistrationId, data, cancellationToken);

        var updated = await repository.GetByIdAsync(request.RegistrationId, cancellationToken)
            ?? throw new NotFoundException(RegMessages.NotFound);
        return updated.ToDto();
    }
}
