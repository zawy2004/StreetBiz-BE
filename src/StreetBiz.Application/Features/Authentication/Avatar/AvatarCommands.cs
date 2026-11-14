using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.Authentication.Avatar;

public sealed record AvatarDto(string? AvatarUrl);

/// <summary>
/// Sets the caller's profile photo. Any signed-in role may have one. The upload has already passed
/// the size limit and the JPG/PNG/WEBP header sniff; here it is cropped to a square JPEG without
/// metadata, stored, and the previous photo is deleted.
/// </summary>
public sealed record SetAvatarCommand(Stream Content) : IRequest<AvatarDto>;

/// <summary>Removes the caller's profile photo; the apps fall back to the initial letter.</summary>
public sealed record RemoveAvatarCommand : IRequest<AvatarDto>;

public sealed class SetAvatarCommandHandler(
    ICurrentUser currentUser,
    IUserAccountRepository users,
    IFileStorage storage,
    IImageProcessor images) : IRequestHandler<SetAvatarCommand, AvatarDto>
{
    public async Task<AvatarDto> Handle(SetAvatarCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired);

        Stream square;
        try
        {
            square = await images.SquareJpegAsync(request.Content, AvatarFiles.SizePixels, cancellationToken);
        }
        catch (InvalidImageException ex)
        {
            throw new ValidationAppException(new Dictionary<string, string[]> { ["File"] = [ex.Message] });
        }

        var fileName = EvidenceFiles.NewFileName(AvatarFiles.Extension);
        await using (square)
        {
            await storage.SaveAsync(AvatarFiles.StoragePath(userId, fileName), square, cancellationToken);
        }

        var url = AvatarFiles.BuildUrl(userId, fileName);
        var previous = await users.SetAvatarUrlAsync(userId, url, cancellationToken);
        await AvatarCleanup.DeleteAsync(storage, userId, previous, cancellationToken);
        return new AvatarDto(url);
    }
}

public sealed class RemoveAvatarCommandHandler(
    ICurrentUser currentUser,
    IUserAccountRepository users,
    IFileStorage storage) : IRequestHandler<RemoveAvatarCommand, AvatarDto>
{
    public async Task<AvatarDto> Handle(RemoveAvatarCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired);
        var previous = await users.SetAvatarUrlAsync(userId, null, cancellationToken);
        await AvatarCleanup.DeleteAsync(storage, userId, previous, cancellationToken);
        return new AvatarDto(null);
    }
}

internal static class AvatarCleanup
{
    /// <summary>Deletes a replaced photo, but only one that is the caller's own avatar file.</summary>
    public static async Task DeleteAsync(IFileStorage storage, long userId, string? url, CancellationToken cancellationToken)
    {
        if (AvatarFiles.TryParseUrl(url, out var owner, out var fileName) && owner == userId)
        {
            await storage.DeleteAsync(AvatarFiles.StoragePath(owner, fileName), cancellationToken);
        }
    }
}
