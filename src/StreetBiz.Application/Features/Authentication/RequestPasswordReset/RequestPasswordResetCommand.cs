using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.Authentication.RequestPasswordReset;

/// <summary>AUTH-05: start verified recovery. Response is uniform whether or not the phone exists.</summary>
public sealed record RequestPasswordResetCommand(string PhoneNumber) : IRequest<Unit>;

public sealed class RequestPasswordResetCommandValidator : AbstractValidator<RequestPasswordResetCommand>
{
    public RequestPasswordResetCommandValidator()
        => RuleFor(x => x.PhoneNumber)
            .Must(p => AuthValidationRules.PhoneRegex().IsMatch(p))
            .WithMessage("Please enter a valid phone number.");
}

public sealed class RequestPasswordResetCommandHandler(
    IUserAccountRepository userRepository,
    IOtpService otpService) : IRequestHandler<RequestPasswordResetCommand, Unit>
{
    public async Task<Unit> Handle(RequestPasswordResetCommand request, CancellationToken cancellationToken)
    {
        // Only send when the phone is registered, but never reveal the outcome (SEC-05).
        if (await userRepository.PhoneExistsAsync(request.PhoneNumber, cancellationToken))
        {
            await otpService.IssueAsync(request.PhoneNumber, OtpPurposes.PasswordReset, cancellationToken);
        }

        return Unit.Value;
    }
}
