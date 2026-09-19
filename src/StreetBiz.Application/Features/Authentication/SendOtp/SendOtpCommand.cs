using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Authentication.RequestPasswordReset;

namespace StreetBiz.Application.Features.Authentication.SendOtp;

/// <summary>AUTH-02: issue a phone OTP for SIGNUP or PASSWORD_RESET.</summary>
public sealed record SendOtpCommand(string PhoneNumber, string Purpose) : IRequest<Unit>;

public sealed class SendOtpCommandValidator : AbstractValidator<SendOtpCommand>
{
    public SendOtpCommandValidator()
    {
        RuleFor(x => x.PhoneNumber)
            .NotEmpty().WithMessage(AppMessages.InvalidPhone)
            .Must(p => AuthValidationRules.PhoneRegex().IsMatch(p))
            .WithMessage(AppMessages.InvalidPhone);

        RuleFor(x => x.Purpose)
            .Must(p => p is OtpPurposes.Signup or OtpPurposes.PasswordReset or OtpPurposes.Login)
            .WithMessage(AppMessages.UnsupportedOtpPurpose);
    }
}

public sealed class SendOtpCommandHandler(
    IOtpService otpService,
    IUserAccountRepository userRepository,
    ISender sender) : IRequestHandler<SendOtpCommand, Unit>
{
    public async Task<Unit> Handle(SendOtpCommand request, CancellationToken cancellationToken)
    {
        if (request.Purpose == OtpPurposes.PasswordReset)
        {
            // Same uniform behaviour as AUTH-05, so this endpoint cannot be used to
            // probe which phones are registered or to text arbitrary numbers.
            return await sender.Send(new RequestPasswordResetCommand(request.PhoneNumber), cancellationToken);
        }

        if (request.Purpose == OtpPurposes.Login)
        {
            // Sign-in codes follow the same rule: only registered phones receive one,
            // and the caller cannot tell the difference (SEC-05).
            if (await userRepository.PhoneExistsAsync(request.PhoneNumber, cancellationToken))
            {
                try
                {
                    await otpService.IssueAsync(request.PhoneNumber, OtpPurposes.Login, cancellationToken);
                }
                catch (TooManyRequestsException)
                {
                    // Only a registered phone can hit the cooldown, so reporting it would leak.
                }
            }

            return Unit.Value;
        }

        // BR-04: tell the user up front instead of after they have typed the code.
        // Registration reveals this anyway (MSG "already registered, sign in instead").
        if (await userRepository.PhoneExistsAsync(request.PhoneNumber, cancellationToken))
        {
            throw new ConflictException(AppMessages.PhoneAlreadyRegistered);
        }

        await otpService.IssueAsync(request.PhoneNumber, request.Purpose, cancellationToken);
        return Unit.Value;
    }
}
