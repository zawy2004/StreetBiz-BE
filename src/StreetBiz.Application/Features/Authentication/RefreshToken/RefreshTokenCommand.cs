using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Authentication;

namespace StreetBiz.Application.Features.Authentication.RefreshToken;

/// <summary>Rotate a refresh token: revoke the old session, issue a new one.</summary>
public sealed record RefreshTokenCommand(string RefreshToken, string? DeviceInfo, string? IpAddress)
    : IRequest<AuthResultDto>;

public sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenCommandValidator()
        => RuleFor(x => x.RefreshToken).NotEmpty().WithMessage(AppMessages.RefreshTokenRequired);
}

public sealed class RefreshTokenCommandHandler(
    IJwtTokenService jwtTokenService,
    ISessionRepository sessionRepository,
    IUserAccountRepository userRepository,
    IDateTimeProvider clock,
    IAuthTokenIssuer tokenIssuer) : IRequestHandler<RefreshTokenCommand, AuthResultDto>
{
    public async Task<AuthResultDto> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var hash = jwtTokenService.HashRefreshToken(request.RefreshToken);
        var session = await sessionRepository.GetActiveByRefreshHashAsync(hash, cancellationToken);

        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= clock.UtcNow)
        {
            throw new AuthenticationException(AppMessages.SessionExpired);
        }

        var user = await userRepository.GetByIdAsync(session.UserId, cancellationToken)
            ?? throw new AuthenticationException(AppMessages.SessionExpired);

        if (user.AccountStatus != AccountStatuses.Active)
        {
            throw new AuthenticationException(AppMessages.AccountSuspended);
        }

        // Rotate: revoke the presented session, then issue a fresh session + tokens.
        await sessionRepository.RevokeAsync(session.Id, cancellationToken);
        return await tokenIssuer.IssueAsync(user, request.DeviceInfo, request.IpAddress, cancellationToken);
    }
}
