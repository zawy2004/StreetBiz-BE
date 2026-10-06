using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.VendorRegistration.GenerateRegistrationDocument;

/// <summary>Mẫu số 01 Phụ lục II, TT 68/2025/TT-BTC, filled with the caller's own registration,
/// as a downloadable .docx or .pdf. Still watermarked "BẢN NHÁP" until the ward approves it.</summary>
public sealed record GenerateRegistrationDocumentQuery(long RegistrationId, string Format)
    : IRequest<(byte[] Content, string FileName)>;

public sealed class GenerateRegistrationDocumentQueryValidator : AbstractValidator<GenerateRegistrationDocumentQuery>
{
    public GenerateRegistrationDocumentQueryValidator()
    {
        RuleFor(x => x.RegistrationId).GreaterThan(0);
        RuleFor(x => x.Format).Must(f => f is "docx" or "pdf").WithMessage("Định dạng phải là docx hoặc pdf.");
    }
}

public sealed class GenerateRegistrationDocumentQueryHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    IUserAccountRepository users,
    IAdministrativeUnitRepository administrativeUnits,
    IRegistrationDocumentGenerator generator)
    : IRequestHandler<GenerateRegistrationDocumentQuery, (byte[] Content, string FileName)>
{
    public async Task<(byte[] Content, string FileName)> Handle(
        GenerateRegistrationDocumentQuery request, CancellationToken cancellationToken)
    {
        var registration = await vendorContext.RequireOwnedRegistrationAsync(request.RegistrationId, cancellationToken);

        var ownerUser = await users.GetByIdAsync(
            currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired), cancellationToken);
        var wards = await administrativeUnits.ListWardsAsync(cancellationToken);
        var wardName = wards.FirstOrDefault(w => w.UnitId == registration.WardUnitId)?.UnitName ?? "UBND Phường";

        var data = new RegistrationDocumentData(
            registration.RegistrationId,
            wardName,
            ownerUser?.FullName ?? registration.DisplayName,
            registration.DisplayName,
            registration.VendorType,
            registration.DeclaredAddress,
            registration.RegistrationStatus,
            registration.OwnerDateOfBirth,
            registration.OwnerGender,
            registration.OwnerEthnicity,
            registration.OwnerNationality,
            IdNumber: null, // not surfaced on the vendor-side domain projection; the officer's copy carries it
            registration.IdType,
            registration.IdIssuedDate,
            registration.IdIssuedPlace,
            registration.PermanentAddress,
            registration.ContactAddress,
            registration.BusinessLine,
            registration.BusinessLineCode,
            registration.CapitalAmount,
            registration.LaborCount,
            registration.PlannedStartDate,
            registration.FoodSafetyCommitmentAt,
            registration.HouseholdMembersOrEmpty
                .Select(m => new RegistrationDocumentHouseholdMember(
                    m.FullName, m.DateOfBirth, m.IdNumber, m.RelationshipToOwner, m.CapitalContribution))
                .ToList(),
            registration.ReviewedAt,
            ReviewedByName: null);

        return await generator.GenerateAsync(data, request.Format, cancellationToken);
    }
}
