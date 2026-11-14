using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Authentication.Avatar;

namespace StreetBiz.Application.Tests;

public sealed class AvatarCommandTests
{
    private const long UserId = 42;
    private static readonly string OldFile = new string('a', 32) + ".jpg";
    private static readonly string OldUrl = AvatarFiles.BuildUrl(UserId, OldFile);

    private readonly Mock<ICurrentUser> currentUser = new();
    private readonly Mock<IUserAccountRepository> users = new();
    private readonly Mock<IFileStorage> storage = new();
    private readonly Mock<IImageProcessor> images = new();

    public AvatarCommandTests()
    {
        currentUser.SetupGet(x => x.UserId).Returns(UserId);
        images.Setup(x => x.SquareJpegAsync(It.IsAny<Stream>(), AvatarFiles.SizePixels, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([0xFF, 0xD8, 0xFF]));
    }

    private SetAvatarCommandHandler SetHandler() => new(currentUser.Object, users.Object, storage.Object, images.Object);

    [Fact]
    public async Task A_new_photo_is_stored_as_the_callers_avatar_and_the_old_file_is_deleted()
    {
        string? savedPath = null;
        storage.Setup(x => x.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stream, CancellationToken>((path, _, _) => savedPath = path)
            .Returns(Task.CompletedTask);
        users.Setup(x => x.SetAvatarUrlAsync(UserId, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(OldUrl);

        var result = await SetHandler().Handle(new SetAvatarCommand(new MemoryStream([1])), default);

        AvatarFiles.TryParseUrl(result.AvatarUrl, out var owner, out var fileName).Should().BeTrue();
        owner.Should().Be(UserId);
        savedPath.Should().Be(AvatarFiles.StoragePath(UserId, fileName));
        users.Verify(x => x.SetAvatarUrlAsync(UserId, result.AvatarUrl, It.IsAny<CancellationToken>()), Times.Once);
        storage.Verify(x => x.DeleteAsync(AvatarFiles.StoragePath(UserId, OldFile), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_undecodable_photo_is_a_field_error_and_nothing_is_saved()
    {
        images.Setup(x => x.SquareJpegAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidImageException("Ảnh bị lỗi."));

        var act = () => SetHandler().Handle(new SetAvatarCommand(new MemoryStream([1])), default);

        (await act.Should().ThrowAsync<ValidationAppException>()).Which.Errors.Should().ContainKey("File");
        storage.Verify(x => x.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
        users.Verify(x => x.SetAvatarUrlAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Removing_clears_the_url_and_deletes_the_file()
    {
        users.Setup(x => x.SetAvatarUrlAsync(UserId, null, It.IsAny<CancellationToken>())).ReturnsAsync(OldUrl);

        var result = await new RemoveAvatarCommandHandler(currentUser.Object, users.Object, storage.Object)
            .Handle(new RemoveAvatarCommand(), default);

        result.AvatarUrl.Should().BeNull();
        storage.Verify(x => x.DeleteAsync(AvatarFiles.StoragePath(UserId, OldFile), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_previous_url_that_is_not_the_callers_own_avatar_file_is_never_deleted()
    {
        // Defensive: a hand-edited row must not let the handler delete somebody else's file.
        users.Setup(x => x.SetAvatarUrlAsync(UserId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AvatarFiles.BuildUrl(7, OldFile));

        await new RemoveAvatarCommandHandler(currentUser.Object, users.Object, storage.Object)
            .Handle(new RemoveAvatarCommand(), default);

        storage.Verify(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_signed_out_caller_is_refused()
    {
        currentUser.SetupGet(x => x.UserId).Returns((long?)null);

        var act = () => SetHandler().Handle(new SetAvatarCommand(new MemoryStream([1])), default);

        await act.Should().ThrowAsync<AuthenticationException>();
    }
}
