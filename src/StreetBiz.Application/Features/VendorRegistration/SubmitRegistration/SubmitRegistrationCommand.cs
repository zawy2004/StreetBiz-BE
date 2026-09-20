using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.VendorRegistration;
using StreetBiz.Application.Features.VendorKyc;

namespace StreetBiz.Application.Features.VendorRegistration.SubmitRegistration;

/// <summary>
/// REG-01: submit a business-registration application.
///
/// Field set mirrors Mẫu số 01 Phụ lục II, Thông tư 68/2025/TT-BTC (Giấy đề nghị đăng ký
/// hộ kinh doanh, effective 01/07/2025, replacing the old Nghị định 01/2021/NĐ-CP form) --
/// not just enough fields for the ward sidewalk-use check. This registration record is what
/// stands in for that real government form inside StreetBiz, so it must carry the owner's
/// legal identity, ngành/nghề kinh doanh, vốn, lao động and (for co-owned households) member
/// capital contributions, plus the food-safety commitment street-food vendors must still make
/// even though they are exempt from the formal ATTP certificate.
/// </summary>
public sealed record SubmitRegistrationCommand(
    string VendorType,
    string DisplayName,
    string? DeclaredAddress,
    decimal? AddressLatitude,
    decimal? AddressLongitude,
    int WardUnitId,
    // ---- Chủ hộ kinh doanh. Defaulted to null so older call sites keep compiling; the
    // validator below is what actually requires these on a real submission. ----
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
    /// <summary>Separate, explicit self-declared commitment to food-safety conditions (BR-46
    /// actor_id + timestamp captured at write time). Not a certificate upload -- individual/
    /// household street-food vendors are exempt from the formal Giấy chứng nhận cơ sở đủ điều
    /// kiện ATTP, but must still commit to meeting food-safety conditions.</summary>
    bool FoodSafetyCommitment = false,
    IReadOnlyList<NewHouseholdMember>? HouseholdMembers = null) : IRequest<BusinessRegistrationDto>;

public sealed class SubmitRegistrationCommandValidator : AbstractValidator<SubmitRegistrationCommand>
{
    public SubmitRegistrationCommandValidator()
    {
        RuleFor(x => x.VendorType)
            .Must(t => VendorTypes.All.Contains(t))
            .WithMessage(RegMessages.SelectVendorType);

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage(RegMessages.DisplayNameRequired)
            .MaximumLength(180).WithMessage(RegMessages.DisplayNameTooLong);

        // BR-07: a fixed storefront must declare an address.
        RuleFor(x => x.DeclaredAddress)
            .NotEmpty()
            .When(x => x.VendorType == VendorTypes.FixedStorefront)
            .WithMessage(RegMessages.FixedNeedsAddress);

        RuleFor(x => x.WardUnitId).GreaterThan(0).WithMessage(AppMessages.InvalidWard);

        // ---- Chủ hộ kinh doanh: required on the real Mẫu số 01 form (RegMessages.OwnerXxx). ----
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

        // ---- Ngành nghề, quy mô hộ kinh doanh ----
        RuleFor(x => x.BusinessLine).NotEmpty().WithMessage(RegMessages.BusinessLineRequired);
        RuleFor(x => x.CapitalAmount).NotNull().GreaterThanOrEqualTo(0)
            .WithMessage(RegMessages.CapitalAmountRequired);
        RuleFor(x => x.LaborCount).NotNull().GreaterThanOrEqualTo(0)
            .WithMessage(RegMessages.LaborCountRequired);
        RuleFor(x => x.PlannedStartDate).NotNull().WithMessage(RegMessages.PlannedStartDateRequired);

        // ---- Cam kết ATTP: bắt buộc riêng biệt, không gộp vào điều khoản chung. ----
        RuleFor(x => x.FoodSafetyCommitment).Equal(true).WithMessage(RegMessages.FoodSafetyCommitmentRequired);

        RuleForEach(x => x.HouseholdMembers).ChildRules(member =>
        {
            member.RuleFor(m => m.FullName).NotEmpty().WithMessage(RegMessages.HouseholdMemberNameRequired);
        });
    }
}

public sealed class SubmitRegistrationCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    IBusinessRegistrationRepository repository,
    IKycResultRepository kycResults,
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
            request.AddressLatitude, request.AddressLongitude, request.WardUnitId,
            request.OwnerDateOfBirth, request.OwnerGender, request.OwnerEthnicity, request.OwnerNationality,
            request.IdType, request.IdIssuedDate, request.IdIssuedPlace, request.PermanentAddress, request.ContactAddress,
            request.BusinessLine, request.BusinessLineCode, request.CapitalAmount, request.LaborCount, request.PlannedStartDate,
            request.FoodSafetyCommitment, request.HouseholdMembers);

        var id = await repository.CreateAsync(vendorId, data, cancellationToken);

        // The eKYC scans ran while this form was still being filled in, so they had no
        // registration to belong to yet. Attach them now, so the reviewing officer sees the
        // scores that were recorded server-side (never ones the client claimed).
        if (currentUser.UserId is { } userId)
        {
            await kycResults.LinkPendingResultsAsync(userId, id, cancellationToken);
        }

        var created = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException(RegMessages.NotFound);

        return created.ToDto();
    }
}
