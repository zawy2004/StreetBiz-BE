using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using StreetBiz.API.Extensions;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Authentication;
using StreetBiz.Application.Features.Authentication.ChangePassword;
using StreetBiz.Application.Features.Authentication.Login;
using StreetBiz.Application.Features.Authentication.LoginWithOtp;
using StreetBiz.Application.Features.Authentication.Logout;
using StreetBiz.Application.Features.Authentication.RefreshToken;
using StreetBiz.Application.Features.Authentication.Register;
using StreetBiz.Application.Features.Authentication.RequestPasswordReset;
using StreetBiz.Application.Features.Authentication.ResetPassword;
using StreetBiz.Application.Features.Authentication.SendOtp;
using StreetBiz.Application.Features.Authentication.Sessions;

namespace StreetBiz.API.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(ISender sender) : ControllerBase
{
    /// <summary>AUTH-02: send a phone OTP (SIGNUP or PASSWORD_RESET).</summary>
    [EnableRateLimiting(AuthRateLimitSetup.OtpRequest)]
    [HttpPost("send-otp")]
    public async Task<IActionResult> SendOtp(SendOtpRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new SendOtpCommand(request.PhoneNumber, request.Purpose), cancellationToken);
        return Ok(new { message = AppMessages.OtpSent });
    }

    /// <summary>AUTH-01: register a new account.</summary>
    [HttpPost("register")]
    public async Task<ActionResult<AuthResultDto>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new RegisterCommand(
            request.PhoneNumber, request.Password, request.FullName, request.RoleCode,
            request.WardUnitId, request.Otp, DeviceInfo(), IpAddress()), cancellationToken);
        return Ok(result);
    }

    /// <summary>AUTH-03: sign in with phone + password.</summary>
    [EnableRateLimiting(AuthRateLimitSetup.SignIn)]
    [HttpPost("login")]
    public async Task<ActionResult<AuthResultDto>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new LoginCommand(request.PhoneNumber, request.Password, DeviceInfo(), IpAddress()), cancellationToken);
        return Ok(result);
    }

    /// <summary>AUTH-03: sign in with phone + OTP, no password (FE-01, BR-03).</summary>
    [EnableRateLimiting(AuthRateLimitSetup.SignIn)]
    [HttpPost("login-otp")]
    public async Task<ActionResult<AuthResultDto>> LoginWithOtp(
        LoginWithOtpRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new LoginWithOtpCommand(request.PhoneNumber, request.Otp, DeviceInfo(), IpAddress()),
            cancellationToken);
        return Ok(result);
    }

    /// <summary>Rotate access/refresh tokens.</summary>
    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResultDto>> Refresh(RefreshRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new RefreshTokenCommand(request.RefreshToken, DeviceInfo(), IpAddress()), cancellationToken);
        return Ok(result);
    }

    /// <summary>AUTH-04: sign out of the current session.</summary>
    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await sender.Send(new LogoutCommand(), cancellationToken);
        return Ok(new { message = AppMessages.SignOutSuccess });
    }

    /// <summary>AUTH-07: change the password.</summary>
    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new ChangePasswordCommand(request.CurrentPassword, request.NewPassword), cancellationToken);
        return Ok(new { message = AppMessages.PasswordChanged });
    }

    /// <summary>AUTH-05: request a password reset (uniform response).</summary>
    [EnableRateLimiting(AuthRateLimitSetup.OtpRequest)]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new RequestPasswordResetCommand(request.PhoneNumber), cancellationToken);
        return Ok(new { message = AppMessages.PasswordResetSent });
    }

    /// <summary>AUTH-06: reset the password with an OTP.</summary>
    [EnableRateLimiting(AuthRateLimitSetup.SignIn)]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(
            new ResetPasswordCommand(request.PhoneNumber, request.Otp, request.NewPassword), cancellationToken);
        return Ok(new { message = AppMessages.PasswordResetSuccess });
    }

    /// <summary>AUTH-08: list the caller's active sessions.</summary>
    [Authorize]
    [HttpGet("sessions")]
    public async Task<ActionResult<IReadOnlyList<SessionDto>>> Sessions(CancellationToken cancellationToken)
        => Ok(await sender.Send(new ListSessionsQuery(), cancellationToken));

    /// <summary>AUTH-09: revoke a specific session.</summary>
    [Authorize]
    [HttpDelete("sessions/{sessionId:long}")]
    public async Task<IActionResult> RevokeSession(long sessionId, CancellationToken cancellationToken)
    {
        await sender.Send(new RevokeSessionCommand(sessionId), cancellationToken);
        return Ok(new { message = AppMessages.SessionRevoked });
    }

    private string? DeviceInfo()
    {
        var agent = Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(agent) ? null : agent[..Math.Min(agent.Length, 256)];
    }

    private string? IpAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
