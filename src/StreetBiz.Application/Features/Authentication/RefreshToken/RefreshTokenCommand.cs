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
    /// <summary>A second presentation of an already-rotated token inside this window is a benign race (two tabs).</summary>
    private static readonly TimeSpan ReuseGrace = TimeSpan.FromSeconds(30);

    public async Task<AuthResultDto> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var hash = jwtTokenService.HashRefreshToken(request.RefreshToken);
        var session = await sessionRepository.GetActiveByRefreshHashAsync(hash, cancellationToken);
        var now = clock.UtcNow;

        if (session is null)
        {
            throw new AuthenticationException(AppMessages.SessionExpired);
        }

        if (session.RevokedAt is { } revokedAt)
        {
            // A rotated token coming back much later means it was copied: treat the whole
            // login family as compromised and sign every session of the user out.
            if (now - revokedAt > ReuseGrace)
            {
                await sessionRepository.RevokeAllForUserAsync(session.UserId, cancellationToken);
            }

            throw new AuthenticationException(AppMessages.SessionExpired);
        }

        if (session.ExpiresAt <= now)
        {
            throw new AuthenticationException(AppMessages.SessionExpired);
        }

        var user = await userRepository.GetByIdAsync(session.UserId, cancellationToken)
            ?? throw new AuthenticationException(AppMessages.SessionExpired);

        if (user.AccountStatus != AccountStatuses.Active)
        {
            throw new AuthenticationException(AppMessages.AccountSuspended);
        }

        // Rotate atomically: of two concurrent refreshes with the same token only one wins.
        if (!await sessionRepository.TryRevokeAsync(session.Id, cancellationToken))
        {
            throw new AuthenticationException(AppMessages.SessionExpired);
        }

        return await tokenIssuer.RotateAsync(user, request.DeviceInfo, request.IpAddress, session.ExpiresAt, cancellationToken);
    }
}
