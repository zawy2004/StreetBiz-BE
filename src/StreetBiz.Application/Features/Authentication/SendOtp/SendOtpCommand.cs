using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.Authentication.SendOtp;

/// <summary>AUTH-02: issue a phone OTP for SIGNUP or PASSWORD_RESET.</summary>
public sealed record SendOtpCommand(string PhoneNumber, string Purpose) : IRequest<Unit>;

public sealed class SendOtpCommandValidator : AbstractValidator<SendOtpCommand>
{
    public SendOtpCommandValidator()
    {
        RuleFor(x => x.PhoneNumber)
            .NotEmpty().WithMessage("Please enter a valid phone number.")
            .Must(p => AuthValidationRules.PhoneRegex().IsMatch(p))
            .WithMessage("Please enter a valid phone number.");

        RuleFor(x => x.Purpose)
            .Must(p => p is OtpPurposes.Signup or OtpPurposes.PasswordReset)
            .WithMessage("Unsupported OTP purpose.");
    }
}

public sealed class SendOtpCommandHandler(IOtpService otpService)
    : IRequestHandler<SendOtpCommand, Unit>
{
    public async Task<Unit> Handle(SendOtpCommand request, CancellationToken cancellationToken)
    {
        await otpService.IssueAsync(request.PhoneNumber, request.Purpose, cancellationToken);
        return Unit.Value;
    }
}
