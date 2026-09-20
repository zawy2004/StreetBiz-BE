using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.VendorKyc;

/// <summary>
/// REG-02 eKYC: reads the applicant's CCCD so the REG-01 form can be pre-filled, and compares
/// their portrait against the photo on that card.
///
/// These run BEFORE the registration row exists (the wizard is still being filled in), so
/// consent cannot be read from BusinessRegistrations.biometric_consent_at yet -- the caller
/// must pass an explicit, unbundled consent with each call (Luật Bảo vệ dữ liệu cá nhân 2025 /
/// Nghị định 356/2025/NĐ-CP). It is persisted onto the registration by the existing REG-02
/// evidence step once the registration is created.
/// </summary>
public sealed record ExtractKycIdCardCommand(
    string FrontFileUrl,
    string? BackFileUrl,
    bool BiometricConsent) : IRequest<KycIdCardExtraction>;

public sealed class ExtractKycIdCardCommandValidator : AbstractValidator<ExtractKycIdCardCommand>
{
    public ExtractKycIdCardCommandValidator()
    {
        RuleFor(x => x.FrontFileUrl)
            .NotEmpty().WithMessage(KycMessages.FrontPhotoRequired)
            .Must(url => EvidenceFiles.TryParseUrl(url, out _, out _))
            .WithMessage(RegMessages.UploadValidDocument);

        RuleFor(x => x.BackFileUrl)
            .Must(url => EvidenceFiles.TryParseUrl(url, out _, out _))
            .When(x => !string.IsNullOrWhiteSpace(x.BackFileUrl))
            .WithMessage(RegMessages.UploadValidDocument);

        RuleFor(x => x.BiometricConsent).Equal(true).WithMessage(KycMessages.ConsentRequired);
    }
}

public sealed class ExtractKycIdCardCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    IFileStorage storage,
    IKycVerificationService kycService,
    IKycResultRepository results) : IRequestHandler<ExtractKycIdCardCommand, KycIdCardExtraction>
{
    public async Task<KycIdCardExtraction> Handle(ExtractKycIdCardCommand request, CancellationToken cancellationToken)
    {
        // Vendors only: this endpoint spends real eKYC provider credits per call.
        await vendorContext.RequireVendorIdAsync(cancellationToken);
        var userId = currentUser.UserId ?? throw new AuthenticationException("No active session.");

        await KycFiles.RequireOwnedAsync(request.FrontFileUrl, userId, storage, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.BackFileUrl))
        {
            await KycFiles.RequireOwnedAsync(request.BackFileUrl, userId, storage, cancellationToken);
        }

        var extraction = await kycService.ExtractIdCardAsync(
            request.FrontFileUrl, request.BackFileUrl, cancellationToken);

        await results.RecordIdCardCheckAsync(userId, extraction, cancellationToken);
        return extraction;
    }
}

/// <summary>REG-02 eKYC: portrait ↔ CCCD photo comparison (FPT.AI Facematch).</summary>
public sealed record MatchKycFaceCommand(
    string SelfieFileUrl,
    string IdCardFrontFileUrl,
    bool BiometricConsent) : IRequest<KycFaceMatchResult>;

public sealed class MatchKycFaceCommandValidator : AbstractValidator<MatchKycFaceCommand>
{
    public MatchKycFaceCommandValidator()
    {
        RuleFor(x => x.SelfieFileUrl)
            .NotEmpty().WithMessage(KycMessages.SelfiePhotoRequired)
            .Must(url => EvidenceFiles.TryParseUrl(url, out _, out _))
            .WithMessage(RegMessages.UploadValidDocument);

        RuleFor(x => x.IdCardFrontFileUrl)
            .NotEmpty().WithMessage(KycMessages.FrontPhotoRequired)
            .Must(url => EvidenceFiles.TryParseUrl(url, out _, out _))
            .WithMessage(RegMessages.UploadValidDocument);

        RuleFor(x => x.BiometricConsent).Equal(true).WithMessage(KycMessages.ConsentRequired);
    }
}

public sealed class MatchKycFaceCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    IFileStorage storage,
    IKycVerificationService kycService,
    IKycResultRepository results) : IRequestHandler<MatchKycFaceCommand, KycFaceMatchResult>
{
    public async Task<KycFaceMatchResult> Handle(MatchKycFaceCommand request, CancellationToken cancellationToken)
    {
        await vendorContext.RequireVendorIdAsync(cancellationToken);
        var userId = currentUser.UserId ?? throw new AuthenticationException("No active session.");

        await KycFiles.RequireOwnedAsync(request.SelfieFileUrl, userId, storage, cancellationToken);
        await KycFiles.RequireOwnedAsync(request.IdCardFrontFileUrl, userId, storage, cancellationToken);

        var result = await kycService.MatchFaceAsync(
            request.SelfieFileUrl, request.IdCardFrontFileUrl, cancellationToken);

        await results.RecordFaceMatchAsync(userId, result, cancellationToken);
        return result;
    }
}

internal static class KycFiles
{
    /// <summary>Refuses a file the caller did not upload -- the same guard REG-02 evidence uses.</summary>
    public static async Task RequireOwnedAsync(
        string fileUrl, long userId, IFileStorage storage, CancellationToken ct)
    {
        EvidenceFiles.TryParseUrl(fileUrl, out var ownerUserId, out var fileName);
        if (ownerUserId != userId || !await storage.ExistsAsync(EvidenceFiles.StoragePath(ownerUserId, fileName), ct))
        {
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["FileUrl"] = [RegMessages.UploadValidDocument],
            });
        }
    }
}
