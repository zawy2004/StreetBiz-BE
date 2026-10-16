using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.API.Controllers;

/// <summary>
/// Photos of a sidewalk slot. Only a ward officer uploads them (they belong to the ward's
/// slot grid); reading is public because vendors and guests browse slots, so these never
/// share storage with the private evidence files.
/// </summary>
[ApiController]
[Route("api/uploads/slot-images")]
public sealed class SlotImagesController(IFileStorage storage, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Uploads one slot photo (JPG/PNG/WEBP, max 5 MB) and returns its URL.</summary>
    [HttpPost]
    [Authorize]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(SlotImageFiles.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = SlotImageFiles.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<UploadedFileResponse>> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired);
        if (currentUser.RoleCode != RoleCodes.WardAuthority)
        {
            throw new ForbiddenException("Chỉ cán bộ phường mới tải được ảnh ô.");
        }

        if (file is null || file.Length == 0)
        {
            throw FileError(SlotImageFiles.InvalidType);
        }

        if (file.Length > SlotImageFiles.MaxBytes)
        {
            throw FileError(EvidenceFiles.TooLarge);
        }

        var header = new byte[12];
        int read;
        await using (var probe = file.OpenReadStream())
        {
            read = await probe.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        }

        var extension = SlotImageFiles.DetectExtension(header.AsSpan(0, read))
            ?? throw FileError(SlotImageFiles.InvalidType);

        var fileName = EvidenceFiles.NewFileName(extension);
        await using (var content = file.OpenReadStream())
        {
            await storage.SaveAsync(SlotImageFiles.StoragePath(userId, fileName), content, cancellationToken);
        }

        return Ok(new UploadedFileResponse(
            SlotImageFiles.BuildUrl(userId, fileName), EvidenceFiles.ContentTypes[extension], file.Length));
    }

    [HttpGet("{ownerUserId:long}/{fileName}")]
    [AllowAnonymous]
    public async Task<IActionResult> Download(long ownerUserId, string fileName, CancellationToken cancellationToken)
    {
        if (!SlotImageFiles.IsValidFileName(fileName))
        {
            return NotFound();
        }

        var stream = await storage.OpenReadAsync(SlotImageFiles.StoragePath(ownerUserId, fileName), cancellationToken);
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
