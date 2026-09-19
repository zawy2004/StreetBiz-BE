using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.Authentication.ResetPassword;

/// <summary>AUTH-06: set a new password after verified recovery; revoke all sessions.</summary>
public sealed record ResetPasswordCommand(string PhoneNumber, string Otp, string NewPassword) : IRequest<Unit>;

public sealed class ResetPasswordCommandValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordCommandValidator()
    {
        RuleFor(x => x.PhoneNumber)
            .Must(p => AuthValidationRules.PhoneRegex().IsMatch(p))
            .WithMessage(AppMessages.InvalidPhone);
        RuleFor(x => x.Otp)
            .Must(o => AuthValidationRules.OtpRegex().IsMatch(o))
            .WithMessage(AppMessages.OtpFormat);
        RuleFor(x => x.NewPassword)
            .Must(p => AuthValidationRules.PasswordRegex().IsMatch(p))
            .WithMessage(AppMessages.NewPasswordPolicy);
    }
}

public sealed class ResetPasswordCommandHandler(
    IOtpService otpService,
    IUserAccountRepository userRepository,
    IPasswordHasher passwordHasher,
    ISessionRepository sessionRepository) : IRequestHandler<ResetPasswordCommand, Unit>
{
    public async Task<Unit> Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        await otpService.ConsumeAsync(request.PhoneNumber, OtpPurposes.PasswordReset, request.Otp, cancellationToken);

        var user = await userRepository.GetByPhoneAsync(request.PhoneNumber, cancellationToken)
            ?? throw new NotFoundException("Account not found.");

        await userRepository.UpdatePasswordHashAsync(
            user.Id, passwordHasher.Hash(request.NewPassword), cancellationToken);

        await sessionRepository.RevokeAllForUserAsync(user.Id, cancellationToken);
        return Unit.Value;
    }
}
