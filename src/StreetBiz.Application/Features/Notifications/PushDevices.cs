using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Application.Features.Notifications;

/// <summary>
/// The phones that receive push notifications for an account (UserDevices). The app registers
/// its Expo push token after sign-in and removes it on sign-out; the server pushes every new
/// Notifications row to the recipient's active devices.
/// </summary>
public interface IUserDeviceRepository
{
    /// <summary>
    /// Saves the token for this install and makes it active. The same token on another account
    /// (the phone was signed in to someone else before) is deactivated, so a phone only ever
    /// receives the notifications of the account signed in on it now.
    /// </summary>
    Task RegisterAsync(long userId, string deviceIdentifier, string platform, string pushToken, CancellationToken cancellationToken);

    /// <summary>Deactivates this install for the user; a device that was never registered is not an error.</summary>
    Task UnregisterAsync(long userId, string deviceIdentifier, CancellationToken cancellationToken);

    /// <summary>Active push tokens of these users.</summary>
    Task<IReadOnlyList<PushTarget>> ActiveTargetsAsync(IReadOnlyCollection<long> userIds, CancellationToken cancellationToken);

    /// <summary>Deactivates tokens the push service reported as no longer valid (the app was removed).</summary>
    Task DeactivateTokensAsync(IReadOnlyCollection<string> pushTokens, CancellationToken cancellationToken);
}

public sealed record PushTarget(long UserId, string PushToken);

/// <summary>One notification to deliver as a push, built from a saved Notifications row.</summary>
public sealed record PushMessage(
    long UserId,
    long NotificationId,
    string Type,
    string Title,
    string Body,
    string? EntityType,
    long? EntityId);

/// <summary>Hands saved notifications to the background push sender without blocking the request.</summary>
public interface IPushQueue
{
    void Enqueue(IEnumerable<PushMessage> messages);
}

public static class PushDeviceRules
{
    public static readonly string[] Platforms = ["ANDROID", "IOS"];

    public const string InvalidToken = "Mã nhận thông báo của thiết bị không hợp lệ.";
    public const string InvalidPlatform = "Nền tảng thiết bị không hợp lệ.";
    public const string InvalidDevice = "Mã thiết bị không hợp lệ.";

    /// <summary>Expo push tokens look like <c>ExponentPushToken[xxxxxxxx]</c> (older apps: <c>ExpoPushToken[...]</c>).</summary>
    public static bool IsExpoToken(string? token) =>
        token is { Length: > 20 and <= 500 }
        && (token.StartsWith("ExponentPushToken[", StringComparison.Ordinal) || token.StartsWith("ExpoPushToken[", StringComparison.Ordinal))
        && token.EndsWith(']');
}

public sealed record RegisterPushDeviceCommand(string DeviceIdentifier, string Platform, string PushToken) : IRequest<Unit>;

public sealed class RegisterPushDeviceCommandValidator : AbstractValidator<RegisterPushDeviceCommand>
{
    public RegisterPushDeviceCommandValidator()
    {
        RuleFor(x => x.DeviceIdentifier).NotEmpty().MaximumLength(180).WithMessage(PushDeviceRules.InvalidDevice);
        RuleFor(x => x.Platform).Must(p => PushDeviceRules.Platforms.Contains(p)).WithMessage(PushDeviceRules.InvalidPlatform);
        RuleFor(x => x.PushToken).Must(PushDeviceRules.IsExpoToken).WithMessage(PushDeviceRules.InvalidToken);
    }
}

public sealed class RegisterPushDeviceCommandHandler(ICurrentUser currentUser, IUserDeviceRepository devices)
    : IRequestHandler<RegisterPushDeviceCommand, Unit>
{
    public async Task<Unit> Handle(RegisterPushDeviceCommand request, CancellationToken cancellationToken)
    {
        var userId = NotificationUser.Require(currentUser);
        await devices.RegisterAsync(userId, request.DeviceIdentifier.Trim(), request.Platform, request.PushToken, cancellationToken);
        return Unit.Value;
    }
}

public sealed record UnregisterPushDeviceCommand(string DeviceIdentifier) : IRequest<Unit>;

public sealed class UnregisterPushDeviceCommandHandler(ICurrentUser currentUser, IUserDeviceRepository devices)
    : IRequestHandler<UnregisterPushDeviceCommand, Unit>
{
    public async Task<Unit> Handle(UnregisterPushDeviceCommand request, CancellationToken cancellationToken)
    {
        var userId = NotificationUser.Require(currentUser);
        await devices.UnregisterAsync(userId, request.DeviceIdentifier.Trim(), cancellationToken);
        return Unit.Value;
    }
}
