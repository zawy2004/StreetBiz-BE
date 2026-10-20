using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.API.Controllers;

/// <summary>
/// Dish photos for the marketplace menu. Uploading is for vendors only; reading is public
/// because guests browse menus, so these never share storage with the private evidence files.
/// </summary>
[ApiController]
[Route("api/uploads/menu-images")]
public sealed class MenuImagesController(IFileStorage storage, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Uploads one dish photo (JPG/PNG/WEBP, max 5 MB) and returns its URL.</summary>
    [HttpPost]
    [Authorize]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MenuImageFiles.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = MenuImageFiles.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<UploadedFileResponse>> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired);
        if (currentUser.RoleCode != RoleCodes.Vendor)
        {
            throw new ForbiddenException(RegMessages.NotAVendor);
        }

        if (file is null || file.Length == 0)
        {
            throw FileError(MenuImageFiles.InvalidType);
        }

        if (file.Length > MenuImageFiles.MaxBytes)
        {
            throw FileError(EvidenceFiles.TooLarge);
        }

        var header = new byte[12];
        int read;
        await using (var probe = file.OpenReadStream())
        {
            read = await probe.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        }

        var extension = MenuImageFiles.DetectExtension(header.AsSpan(0, read))
            ?? throw FileError(MenuImageFiles.InvalidType);

        var fileName = EvidenceFiles.NewFileName(extension);
        await using (var content = file.OpenReadStream())
        {
            await storage.SaveAsync(MenuImageFiles.StoragePath(userId, fileName), content, cancellationToken);
        }

        return Ok(new UploadedFileResponse(
            MenuImageFiles.BuildUrl(userId, fileName), EvidenceFiles.ContentTypes[extension], file.Length));
    }

    [HttpGet("{ownerUserId:long}/{fileName}")]
    [AllowAnonymous]
    public async Task<IActionResult> Download(long ownerUserId, string fileName, CancellationToken cancellationToken)
    {
        if (!MenuImageFiles.IsValidFileName(fileName))
        {
            return NotFound();
        }

        var stream = await storage.OpenReadAsync(MenuImageFiles.StoragePath(ownerUserId, fileName), cancellationToken);
        if (stream is null)
        {
            return NotFound();
        }

        // File names are random and never reused, so a photo can be cached for long.
        Response.Headers.CacheControl = "public, max-age=604800, immutable";
        return File(stream, EvidenceFiles.ContentTypes[Path.GetExtension(fileName)]);
    }

    private static ValidationAppException FileError(string message) =>
        new(new Dictionary<string, string[]> { ["File"] = [message] });
}
