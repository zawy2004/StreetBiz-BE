using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.API.Controllers;

/// <summary>
/// REG-02 evidence files. Identity documents are personal data, so files are never
/// served statically: every download goes through an ownership / role check.
/// </summary>
[ApiController]
[Authorize]
[Route("api/uploads/evidence")]
public sealed class UploadsController(
    IFileStorage storage,
    ICurrentUser currentUser,
    IWardActorResolver wardActors,
    IBusinessRegistrationRepository registrations) : ControllerBase
{

    /// <summary>Uploads one evidence file (JPG/PNG/WEBP/PDF, max 5 MB) and returns its URL.</summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(EvidenceFiles.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = EvidenceFiles.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<UploadedFileResponse>> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired);

        // REG-02 (vendor registration evidence) and WARD-11/12 (on-site
        // inspection photos, violation evidence) are the only callers; scoping
        // to these two roles stops others from filling the disk with files
        // that can never be attached to anything.
        if (currentUser.RoleCode != RoleCodes.Vendor && currentUser.RoleCode != RoleCodes.WardAuthority)
        {
            throw new ForbiddenException(RegMessages.NotAVendor);
        }

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

    /// <summary>
    /// Downloads an evidence file. PRI-02/PRI-07: only the owning vendor and the ward
    /// officer whose ward the registration belongs to. Platform Administrator is
    /// deliberately excluded — BR-44 keeps that role out of registration identity
    /// evidence entirely.
    /// </summary>
    [HttpGet("{ownerUserId:long}/{fileName}")]
    public async Task<IActionResult> Download(long ownerUserId, string fileName, CancellationToken cancellationToken)
    {
        if (!EvidenceFiles.IsValidFileName(fileName))
        {
            return NotFound();
        }

        if (currentUser.UserId != ownerUserId
            && !await IsReviewingWardOfficerAsync(ownerUserId, fileName, cancellationToken))
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

    private async Task<bool> IsReviewingWardOfficerAsync(
        long ownerUserId,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || currentUser.RoleCode != RoleCodes.WardAuthority)
        {
            return false;
        }

        var actor = await wardActors.ResolveAsync(userId, cancellationToken);
        return actor is not null
            && await registrations.EvidenceBelongsToWardAsync(
                ownerUserId, EvidenceFiles.BuildUrl(ownerUserId, fileName), actor.WardId, cancellationToken);
    }

    private static ValidationAppException FileError(string message) =>
        new(new Dictionary<string, string[]> { ["File"] = [message] });
}

public sealed record UploadedFileResponse(string FileUrl, string ContentType, long SizeBytes);
