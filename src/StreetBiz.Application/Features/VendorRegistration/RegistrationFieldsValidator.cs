using FluentValidation;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.VendorRegistration;

/// <summary>The form fields shared by REG-01 (submit) and REG-04 (update).</summary>
public interface IRegistrationFields
{
    string VendorType { get; }
    string DisplayName { get; }
    string? DeclaredAddress { get; }
    decimal? AddressLatitude { get; }
    decimal? AddressLongitude { get; }
    int WardUnitId { get; }
    DateOnly? OwnerDateOfBirth { get; }
    string? OwnerGender { get; }
    string? OwnerEthnicity { get; }
    string? OwnerNationality { get; }
    string? IdType { get; }
    DateOnly? IdIssuedDate { get; }
    string? IdIssuedPlace { get; }
    string? PermanentAddress { get; }
    string? ContactAddress { get; }
    string? BusinessLine { get; }
    string? BusinessLineCode { get; }
    decimal? CapitalAmount { get; }
    int? LaborCount { get; }
    DateOnly? PlannedStartDate { get; }
    bool FoodSafetyCommitment { get; }
    IReadOnlyList<NewHouseholdMember>? HouseholdMembers { get; }
}

/// <summary>
/// One rule set for submit and update, so an edit can never slip in data a first submission would
/// have refused (the update validator used to omit the food-safety commitment and every length
/// limit, turning over-long text into a database error instead of a 400).
/// Limits mirror the column sizes in db/StreetBiz_SQL_Server.sql.
/// </summary>
public class RegistrationFieldsValidator<T> : AbstractValidator<T> where T : IRegistrationFields
{
    private const int AdultYears = 18;

    public RegistrationFieldsValidator()
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
        RuleFor(x => x.DeclaredAddress).MaximumLength(500).WithMessage(RegMessages.FieldTooLong);

        RuleFor(x => x.WardUnitId).GreaterThan(0).WithMessage(AppMessages.InvalidWard);

        RuleFor(x => x.AddressLatitude)
            .InclusiveBetween(-90m, 90m).When(x => x.AddressLatitude is not null)
            .WithMessage(RegMessages.CoordinatesInvalid);
        RuleFor(x => x.AddressLongitude)
            .InclusiveBetween(-180m, 180m).When(x => x.AddressLongitude is not null)
            .WithMessage(RegMessages.CoordinatesInvalid);

        // ---- Chủ hộ kinh doanh: required on the real Mẫu số 01 form (RegMessages.OwnerXxx). ----
        RuleFor(x => x.OwnerDateOfBirth).NotNull().WithMessage(RegMessages.OwnerDateOfBirthRequired);
        RuleFor(x => x.OwnerDateOfBirth)
            .Must(d => d!.Value <= Today.AddYears(-AdultYears)).When(x => x.OwnerDateOfBirth is not null)
            .WithMessage(RegMessages.OwnerTooYoung);
        RuleFor(x => x.OwnerGender)
            .Must(g => g != null && OwnerGenders.All.Contains(g))
            .WithMessage(RegMessages.OwnerGenderRequired);
        RuleFor(x => x.OwnerEthnicity).MaximumLength(50).WithMessage(RegMessages.FieldTooLong);
        RuleFor(x => x.OwnerNationality)
            .NotEmpty().WithMessage(RegMessages.OwnerNationalityRequired)
            .MaximumLength(50).WithMessage(RegMessages.FieldTooLong);
        RuleFor(x => x.IdType)
            .Must(t => t != null && OwnerIdTypes.All.Contains(t))
            .WithMessage(RegMessages.IdTypeRequired);
        RuleFor(x => x.IdIssuedDate).NotNull().WithMessage(RegMessages.IdIssuedDateRequired);
        RuleFor(x => x.IdIssuedDate)
            .Must(d => d!.Value <= Today).When(x => x.IdIssuedDate is not null)
            .WithMessage(RegMessages.DateMustBePast);
        RuleFor(x => x.IdIssuedPlace)
            .NotEmpty().WithMessage(RegMessages.IdIssuedPlaceRequired)
            .MaximumLength(150).WithMessage(RegMessages.FieldTooLong);
        RuleFor(x => x.PermanentAddress)
            .NotEmpty().WithMessage(RegMessages.PermanentAddressRequired)
            .MaximumLength(300).WithMessage(RegMessages.FieldTooLong);
        RuleFor(x => x.ContactAddress).MaximumLength(300).WithMessage(RegMessages.FieldTooLong);

        // ---- Ngành nghề, quy mô hộ kinh doanh ----
        RuleFor(x => x.BusinessLine)
            .NotEmpty().WithMessage(RegMessages.BusinessLineRequired)
            .MaximumLength(300).WithMessage(RegMessages.FieldTooLong);
        RuleFor(x => x.BusinessLineCode).MaximumLength(20).WithMessage(RegMessages.FieldTooLong);
        RuleFor(x => x.CapitalAmount).NotNull().GreaterThanOrEqualTo(0)
            .WithMessage(RegMessages.CapitalAmountRequired);
        RuleFor(x => x.LaborCount).NotNull().GreaterThanOrEqualTo(0)
            .WithMessage(RegMessages.LaborCountRequired);
        RuleFor(x => x.PlannedStartDate).NotNull().WithMessage(RegMessages.PlannedStartDateRequired);

        // ---- Cam kết ATTP: bắt buộc riêng biệt, không gộp vào điều khoản chung. ----
        RuleFor(x => x.FoodSafetyCommitment).Equal(true).WithMessage(RegMessages.FoodSafetyCommitmentRequired);

        RuleForEach(x => x.HouseholdMembers).ChildRules(member =>
        {
            member.RuleFor(m => m.FullName)
                .NotEmpty().WithMessage(RegMessages.HouseholdMemberNameRequired)
                .MaximumLength(150).WithMessage(RegMessages.FieldTooLong);
            member.RuleFor(m => m.IdNumber).MaximumLength(12).WithMessage(RegMessages.FieldTooLong);
            member.RuleFor(m => m.RelationshipToOwner).MaximumLength(50).WithMessage(RegMessages.FieldTooLong);
            member.RuleFor(m => m.DateOfBirth)
                .Must(d => d!.Value <= Today).When(m => m.DateOfBirth is not null)
                .WithMessage(RegMessages.DateMustBePast);
        });
    }

    // Vietnam time (UTC+7), so a birthday "today" is not rejected for the first 7 hours of the day.
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
}
