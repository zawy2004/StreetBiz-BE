using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.API.Controllers;

/// <summary>
/// REG-02 evidence files. Identity documents are personal data, so files are never
/// served statically: every download goes through an ownership / role check.
/// </summary>
[ApiController]
[Authorize]
[Route("api/uploads/evidence")]
public sealed class UploadsController(IFileStorage storage, ICurrentUser currentUser) : ControllerBase
{
    private static readonly string[] ReviewerRoles = [RoleCodes.WardAuthority, RoleCodes.PlatformAdmin];

    /// <summary>Uploads one evidence file (JPG/PNG/WEBP/PDF, max 5 MB) and returns its URL.</summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(EvidenceFiles.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = EvidenceFiles.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<UploadedFileResponse>> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new AuthenticationException("No active session.");

        if (file is null || file.Length == 0)
        {
            throw FileError(RegMessages.UploadValidDocument);
        }

        if (file.Length > EvidenceFiles.MaxBytes)
        {
            throw FileError(EvidenceFiles.TooLarge);
        }

        var header = new byte[12];
        int read;
        await using (var probe = file.OpenReadStream())
        {
            read = await probe.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        }

        var extension = EvidenceFiles.DetectExtension(header.AsSpan(0, read))
            ?? throw FileError(EvidenceFiles.InvalidType);

        var fileName = EvidenceFiles.NewFileName(extension);
        await using (var content = file.OpenReadStream())
        {
            await storage.SaveAsync(EvidenceFiles.StoragePath(userId, fileName), content, cancellationToken);
        }

        return Ok(new UploadedFileResponse(
            EvidenceFiles.BuildUrl(userId, fileName), EvidenceFiles.ContentTypes[extension], file.Length));
    }

    /// <summary>Downloads an evidence file: its owner, ward authorities and platform admins only.</summary>
    [HttpGet("{ownerUserId:long}/{fileName}")]
    public async Task<IActionResult> Download(long ownerUserId, string fileName, CancellationToken cancellationToken)
    {
        if (!EvidenceFiles.IsValidFileName(fileName))
        {
            return NotFound();
        }

        var isOwner = currentUser.UserId == ownerUserId;
        if (!isOwner && !ReviewerRoles.Contains(currentUser.RoleCode))
        {
            throw new ForbiddenException(AppMessages.Forbidden);
        }

        var stream = await storage.OpenReadAsync(EvidenceFiles.StoragePath(ownerUserId, fileName), cancellationToken);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, no-store";
        return File(stream, EvidenceFiles.ContentTypes[Path.GetExtension(fileName)]);
    }

    private static ValidationAppException FileError(string message) =>
        new(new Dictionary<string, string[]> { ["File"] = [message] });
}

public sealed record UploadedFileResponse(string FileUrl, string ContentType, long SizeBytes);
