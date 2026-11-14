using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Authentication.Avatar;

namespace StreetBiz.API.Controllers;

/// <summary>The signed-in user's own profile photo: upload (replaces the old one) or remove.</summary>
[ApiController]
[Authorize]
[Route("api/account/avatar")]
public sealed class AccountAvatarController(ISender sender) : ControllerBase
{
    /// <summary>Uploads a profile photo (JPG/PNG/WEBP, max 5 MB); the server stores a 256x256 JPEG.</summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(AvatarFiles.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = AvatarFiles.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<AvatarDto>> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            throw FileError(AppMessages.AvatarRequired);
        }

        if (file.Length > AvatarFiles.MaxBytes)
        {
            throw FileError(EvidenceFiles.TooLarge);
        }

        var header = new byte[12];
        int read;
        await using (var probe = file.OpenReadStream())
        {
            read = await probe.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        }

        if (MenuImageFiles.DetectExtension(header.AsSpan(0, read)) is null)
        {
            throw FileError(MenuImageFiles.InvalidType);
        }

        await using var content = file.OpenReadStream();
        return Ok(await sender.Send(new SetAvatarCommand(content), cancellationToken));
    }

    [HttpDelete]
    public async Task<ActionResult<AvatarDto>> Remove(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new RemoveAvatarCommand(), cancellationToken));

    private static ValidationAppException FileError(string message) =>
        new(new Dictionary<string, string[]> { ["File"] = [message] });
}

/// <summary>Serves profile photos. Public: they appear in chat threads and account lists.</summary>
[ApiController]
[Route("api/uploads/avatars")]
public sealed class AvatarImagesController(IFileStorage storage) : ControllerBase
{
    [HttpGet("{userId:long}/{fileName}")]
    [AllowAnonymous]
    public async Task<IActionResult> Download(long userId, string fileName, CancellationToken cancellationToken)
    {
        if (!AvatarFiles.IsValidFileName(fileName))
        {
            return NotFound();
        }

        var stream = await storage.OpenReadAsync(AvatarFiles.StoragePath(userId, fileName), cancellationToken);
        if (stream is null)
        {
            return NotFound();
        }

        // A new photo always gets a new random name, so the old URL can be cached for long.
        Response.Headers.CacheControl = "public, max-age=604800, immutable";
        return File(stream, AvatarFiles.ContentType);
    }
}
