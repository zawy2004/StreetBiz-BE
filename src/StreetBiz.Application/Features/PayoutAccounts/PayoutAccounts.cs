using System.Security.Cryptography;
using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.PayoutAccounts;

/// <summary>
/// A receiving account as its owner sees it. The keys are never returned; the Client ID and the
/// bank account number are masked. <c>WebhookConfigured</c> is false when the server has no
/// public URL for payOS to call (local development): payments are then settled by asking payOS.
/// </summary>
public sealed record PayoutAccountDto(
    bool Connected,
    string? Status,
    string Provider,
    string? BankName,
    string? AccountNumberMasked,
    string? AccountName,
    string? ClientIdMasked,
    bool WebhookConfirmed,
    bool WebhookConfigured,
    DateTime? LastCheckedAt,
    string? LastError,
    DateTime? ConnectedAt,
    DateTime? UpdatedAt);

/// <summary>The three keys of a payOS channel ("Kênh thanh toán" in my.payos.vn).</summary>
public sealed record ConnectPayoutAccountRequest(string ClientId, string ApiKey, string ChecksumKey);

/// <summary>Whose receiving account a request is about.</summary>
public sealed record PayoutOwner(string OwnerType, long? VendorId, int? WardId, long UserId);

/// <summary>Connects, checks and disconnects the receiving account of a household or a ward.</summary>
public sealed class PayoutAccountService(
    IPayeeAccountRepository payees,
    IPayeeChannelGateway channel,
    ICredentialProtector protector,
    TimeProvider clock)
{
    public const string ProviderName = "payOS";

    public async Task<PayoutAccountDto> GetAsync(PayoutOwner owner, CancellationToken cancellationToken) =>
        ToDto(await FindAsync(owner, cancellationToken));

    public async Task<PayoutAccountDto> ConnectAsync(
        PayoutOwner owner, ConnectPayoutAccountRequest request, CancellationToken cancellationToken)
    {
        if (!protector.IsConfigured)
        {
            throw new DomainRuleException(
                "Máy chủ chưa cấu hình khoá mã hoá cho tài khoản nhận tiền (Payments:PayOs:CredentialKey). " +
                "Liên hệ quản trị viên.");
        }

        var existing = await FindAsync(owner, cancellationToken);
        // The token is kept across reconnects, so the URL registered on payOS stays the same.
        var token = existing?.WebhookToken ?? NewWebhookToken();
        var credentials = new PayeeCredentials(
            existing?.PayeeAccountId ?? 0, request.ClientId.Trim(), request.ApiKey.Trim(), request.ChecksumKey.Trim());
        var info = await channel.VerifyChannelAsync(credentials, channel.WebhookUrlFor(token), cancellationToken);

        var saved = await payees.SaveAsync(
            new SavePayeeAccount(
                owner.OwnerType, owner.VendorId, owner.WardId,
                credentials.ClientId, credentials.ApiKey, credentials.ChecksumKey, token, info),
            owner.UserId,
            clock.GetUtcNow().UtcDateTime,
            cancellationToken);
        return ToDto(saved);
    }

    /// <summary>
    /// Asks payOS again with the stored keys. Refused keys put the account in ERROR (shown in the
    /// answer, not thrown); keys accepted again bring an ERROR account back to ACTIVE.
    /// </summary>
    public async Task<PayoutAccountDto> CheckAsync(PayoutOwner owner, CancellationToken cancellationToken)
    {
        var account = await FindAsync(owner, cancellationToken)
            ?? throw new NotFoundException("Chưa kết nối tài khoản nhận tiền.");
        if (account.Status == PayeeAccountStatuses.Disabled)
        {
            throw new DomainRuleException("Tài khoản nhận tiền đã ngắt kết nối. Hãy kết nối lại.");
        }

        var credentials = await payees.GetCredentialsAsync(account.PayeeAccountId, cancellationToken)
            ?? throw new NotFoundException("Chưa kết nối tài khoản nhận tiền.");
        var now = clock.GetUtcNow().UtcDateTime;
        try
        {
            var info = await channel.VerifyChannelAsync(
                credentials, channel.WebhookUrlFor(account.WebhookToken), cancellationToken);
            await payees.MarkCheckedAsync(account.PayeeAccountId, info, now, cancellationToken);
        }
        catch (PayeeChannelException exception) when (exception.Unauthorized)
        {
            await payees.MarkErrorAsync(account.PayeeAccountId, exception.Message, now, cancellationToken);
        }

        return ToDto(await FindAsync(owner, cancellationToken));
    }

    public async Task<PayoutAccountDto> DisconnectAsync(PayoutOwner owner, CancellationToken cancellationToken)
    {
        var account = await FindAsync(owner, cancellationToken)
            ?? throw new NotFoundException("Chưa kết nối tài khoản nhận tiền.");
        await payees.DisableAsync(account.PayeeAccountId, owner.UserId, clock.GetUtcNow().UtcDateTime, cancellationToken);
        return ToDto(await FindAsync(owner, cancellationToken));
    }

    private Task<PayeeAccountRow?> FindAsync(PayoutOwner owner, CancellationToken cancellationToken) =>
        owner.OwnerType == PayeeOwnerTypes.Vendor
            ? payees.GetForVendorAsync(owner.VendorId!.Value, cancellationToken)
            : payees.GetForWardAsync(owner.WardId!.Value, cancellationToken);

    private PayoutAccountDto ToDto(PayeeAccountRow? account)
    {
        var webhookConfigured = channel.WebhookUrlFor("check") is not null;
        return account is null || account.Status == PayeeAccountStatuses.Disabled
            ? new PayoutAccountDto(false, account?.Status, ProviderName, null, null, null, null, false, webhookConfigured,
                null, null, null, account?.UpdatedAt)
            : new PayoutAccountDto(
                true,
                account.Status,
                ProviderName,
                account.BankName,
                account.AccountNumber is null ? null : PayeeMasks.Tail(account.AccountNumber),
                account.AccountName,
                PayeeMasks.Tail(account.ClientId),
                account.WebhookConfirmedAt is not null,
                webhookConfigured,
                account.LastCheckedAt,
                account.LastError,
                account.CreatedAt,
                account.UpdatedAt);
    }

    /// <summary>32 random bytes, base64url: the unguessable part of the account's webhook URL.</summary>
    private static string NewWebhookToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class ConnectPayoutAccountRequestValidator : AbstractValidator<ConnectPayoutAccountRequest>
{
    public ConnectPayoutAccountRequestValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty().WithMessage("Nhập Client ID của kênh payOS.").MaximumLength(100);
        RuleFor(x => x.ApiKey).NotEmpty().WithMessage("Nhập API Key của kênh payOS.").MaximumLength(200);
        RuleFor(x => x.ChecksumKey).NotEmpty().WithMessage("Nhập Checksum Key của kênh payOS.").MaximumLength(200);
    }
}

#region Household (vendor)

public sealed record GetVendorPayoutAccountQuery : IRequest<PayoutAccountDto>;

public sealed record ConnectVendorPayoutAccountCommand(ConnectPayoutAccountRequest Request) : IRequest<PayoutAccountDto>;

public sealed class ConnectVendorPayoutAccountCommandValidator : AbstractValidator<ConnectVendorPayoutAccountCommand>
{
    public ConnectVendorPayoutAccountCommandValidator() =>
        RuleFor(x => x.Request).NotNull().SetValidator(new ConnectPayoutAccountRequestValidator());
}

public sealed record CheckVendorPayoutAccountCommand : IRequest<PayoutAccountDto>;

public sealed record DisconnectVendorPayoutAccountCommand : IRequest<PayoutAccountDto>;

public sealed class VendorPayoutAccountHandlers(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    PayoutAccountService service) :
    IRequestHandler<GetVendorPayoutAccountQuery, PayoutAccountDto>,
    IRequestHandler<ConnectVendorPayoutAccountCommand, PayoutAccountDto>,
    IRequestHandler<CheckVendorPayoutAccountCommand, PayoutAccountDto>,
    IRequestHandler<DisconnectVendorPayoutAccountCommand, PayoutAccountDto>
{
    public async Task<PayoutAccountDto> Handle(GetVendorPayoutAccountQuery request, CancellationToken cancellationToken) =>
        await service.GetAsync(await OwnerAsync(cancellationToken), cancellationToken);

    public async Task<PayoutAccountDto> Handle(ConnectVendorPayoutAccountCommand request, CancellationToken cancellationToken) =>
        await service.ConnectAsync(await OwnerAsync(cancellationToken), request.Request, cancellationToken);

    public async Task<PayoutAccountDto> Handle(CheckVendorPayoutAccountCommand request, CancellationToken cancellationToken) =>
        await service.CheckAsync(await OwnerAsync(cancellationToken), cancellationToken);

    public async Task<PayoutAccountDto> Handle(DisconnectVendorPayoutAccountCommand request, CancellationToken cancellationToken) =>
        await service.DisconnectAsync(await OwnerAsync(cancellationToken), cancellationToken);

    private async Task<PayoutOwner> OwnerAsync(CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var userId = currentUser.UserId ?? throw new AuthenticationException("Phiên đăng nhập không hợp lệ.");
        return new PayoutOwner(PayeeOwnerTypes.Vendor, vendorId, null, userId);
    }
}

#endregion

#region Ward

public sealed record GetWardPayoutAccountQuery : IRequest<PayoutAccountDto>;

public sealed record ConnectWardPayoutAccountCommand(ConnectPayoutAccountRequest Request) : IRequest<PayoutAccountDto>;

public sealed class ConnectWardPayoutAccountCommandValidator : AbstractValidator<ConnectWardPayoutAccountCommand>
{
    public ConnectWardPayoutAccountCommandValidator() =>
        RuleFor(x => x.Request).NotNull().SetValidator(new ConnectPayoutAccountRequestValidator());
}

public sealed record CheckWardPayoutAccountCommand : IRequest<PayoutAccountDto>;

public sealed record DisconnectWardPayoutAccountCommand : IRequest<PayoutAccountDto>;

/// <summary>Any officer of the ward manages the ward's receiving account for rental fees.</summary>
public sealed class WardPayoutAccountHandlers(
    IWardActorContext actorContext,
    PayoutAccountService service) :
    IRequestHandler<GetWardPayoutAccountQuery, PayoutAccountDto>,
    IRequestHandler<ConnectWardPayoutAccountCommand, PayoutAccountDto>,
    IRequestHandler<CheckWardPayoutAccountCommand, PayoutAccountDto>,
    IRequestHandler<DisconnectWardPayoutAccountCommand, PayoutAccountDto>
{
    public async Task<PayoutAccountDto> Handle(GetWardPayoutAccountQuery request, CancellationToken cancellationToken) =>
        await service.GetAsync(await OwnerAsync(cancellationToken), cancellationToken);

    public async Task<PayoutAccountDto> Handle(ConnectWardPayoutAccountCommand request, CancellationToken cancellationToken) =>
        await service.ConnectAsync(await OwnerAsync(cancellationToken), request.Request, cancellationToken);

    public async Task<PayoutAccountDto> Handle(CheckWardPayoutAccountCommand request, CancellationToken cancellationToken) =>
        await service.CheckAsync(await OwnerAsync(cancellationToken), cancellationToken);

    public async Task<PayoutAccountDto> Handle(DisconnectWardPayoutAccountCommand request, CancellationToken cancellationToken) =>
        await service.DisconnectAsync(await OwnerAsync(cancellationToken), cancellationToken);

    private async Task<PayoutOwner> OwnerAsync(CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return new PayoutOwner(PayeeOwnerTypes.Ward, null, actor.WardId, actor.UserId);
    }
}

#endregion
