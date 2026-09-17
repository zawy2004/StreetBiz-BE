using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Authentication;

namespace StreetBiz.Application.Features.Authentication.Register;

/// <summary>AUTH-01: register a new account bound to a verified phone number.</summary>
public sealed record RegisterCommand(
    string PhoneNumber,
    string Password,
    string? FullName,
    string RoleCode,
    int? WardUnitId,
    string Otp,
    string? DeviceInfo,
    string? IpAddress) : IRequest<AuthResultDto>;

public sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(x => x.PhoneNumber)
            .NotEmpty().Must(p => AuthValidationRules.PhoneRegex().IsMatch(p))
            .WithMessage("Please enter a valid phone number.");

        RuleFor(x => x.Password)
            .NotEmpty().Must(p => AuthValidationRules.PasswordRegex().IsMatch(p))
            .WithMessage("Password must be at least 8 characters and include upper, lower, number and special characters.");

        RuleFor(x => x.RoleCode)
            .Must(r => RoleCodes.SelfRegisterable.Contains(r))
            .WithMessage("You can only register as a Customer or Vendor.");

        RuleFor(x => x.Otp)
            .Must(o => AuthValidationRules.OtpRegex().IsMatch(o))
            .WithMessage("The verification code must be 6 digits.");

        RuleFor(x => x.FullName).MaximumLength(150);
    }
}

public sealed class RegisterCommandHandler(
    IOtpService otpService,
    IUserAccountRepository userRepository,
    IPasswordHasher passwordHasher,
    IDateTimeProvider clock,
    IAuthTokenIssuer tokenIssuer,
    IAdministrativeUnitRepository units) : IRequestHandler<RegisterCommand, AuthResultDto>
{
    public async Task<AuthResultDto> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        // Checked before the OTP is spent, so a bad ward does not burn the code.
        if (request.WardUnitId is { } wardUnitId)
        {
            await units.EnsureWardAsync(wardUnitId, cancellationToken);
        }

        // 1) Prove phone ownership (consumes the SIGNUP challenge).
        await otpService.ConsumeAsync(request.PhoneNumber, OtpPurposes.Signup, request.Otp, cancellationToken);

        // 2) Enforce unique phone (BR-04).
        if (await userRepository.PhoneExistsAsync(request.PhoneNumber, cancellationToken))
        {
            throw new ConflictException(AppMessages.PhoneAlreadyRegistered);
        }

        // 3) Create the account (ACTIVE, phone verified). Vendor row is created for VENDOR role.
        var now = clock.UtcNow;
        var newUser = new NewUser(
            request.PhoneNumber,
            passwordHasher.Hash(request.Password),
            request.FullName,
            request.RoleCode,
            request.WardUnitId,
            AccountStatuses.Active,
            now);

        var userId = await userRepository.CreateAsync(newUser, cancellationToken);

        var user = new AppUser(
            userId, request.PhoneNumber, newUser.PasswordHash, request.FullName,
            request.RoleCode, request.WardUnitId, AccountStatuses.Active, now);

        // 4) Sign in immediately.
        return await tokenIssuer.IssueAsync(user, request.DeviceInfo, request.IpAddress, cancellationToken);
    }
}
