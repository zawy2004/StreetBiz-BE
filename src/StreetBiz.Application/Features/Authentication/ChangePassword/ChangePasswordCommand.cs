using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.Authentication.ChangePassword;

/// <summary>AUTH-07: replace the password after confirming the current one.</summary>
public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword) : IRequest<Unit>;

public sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().WithMessage("Your current password is required.");
        RuleFor(x => x.NewPassword)
            .Must(p => AuthValidationRules.PasswordRegex().IsMatch(p))
            .WithMessage("The new password does not meet the security requirements.");
        RuleFor(x => x.NewPassword)
            .NotEqual(x => x.CurrentPassword)
            .WithMessage("The new password must be different from the current password.");
    }
}

public sealed class ChangePasswordCommandHandler(
    ICurrentUser currentUser,
    IUserAccountRepository userRepository,
    IPasswordHasher passwordHasher,
    ISessionRepository sessionRepository) : IRequestHandler<ChangePasswordCommand, Unit>
{
    public async Task<Unit> Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new AuthenticationException("No active session.");
        var user = await userRepository.GetByIdAsync(userId, cancellationToken)
            ?? throw new NotFoundException("Account not found.");

        if (!passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            // A wrong current password is a form error, not a failed authentication: a 401
            // here would make clients treat the access token as expired and refresh it.
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                [nameof(request.CurrentPassword)] = [AppMessages.CurrentPasswordIncorrect],
            });
        }

        await userRepository.UpdatePasswordHashAsync(
            userId, passwordHasher.Hash(request.NewPassword), cancellationToken);

        // Contain compromise: sign out other devices, keep the current one.
        if (currentUser.SessionId is { } sessionId)
        {
            await sessionRepository.RevokeAllForUserExceptAsync(userId, sessionId, cancellationToken);
        }

        return Unit.Value;
    }
}
