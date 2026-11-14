using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Features.Notifications;

namespace StreetBiz.API.Controllers;

/// <summary>
/// The signed-in user's phones for push notifications. The mobile app registers its Expo push
/// token after sign-in and removes the device before signing out.
/// </summary>
[ApiController]
[Authorize]
[Route("api/devices")]
public sealed class DevicesController(ISender sender) : ControllerBase
{
    /// <summary>Registers (or refreshes) this install's push token for the caller.</summary>
    [HttpPost]
    public async Task<IActionResult> Register(RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new RegisterPushDeviceCommand(request.DeviceIdentifier, request.Platform, request.PushToken), cancellationToken);
        return NoContent();
    }

    /// <summary>Stops pushes to this install for the caller (sign-out).</summary>
    [HttpDelete("{deviceIdentifier}")]
    public async Task<IActionResult> Unregister(string deviceIdentifier, CancellationToken cancellationToken)
    {
        await sender.Send(new UnregisterPushDeviceCommand(deviceIdentifier), cancellationToken);
        return NoContent();
    }
}

public sealed record RegisterDeviceRequest(string DeviceIdentifier, string Platform, string PushToken);
