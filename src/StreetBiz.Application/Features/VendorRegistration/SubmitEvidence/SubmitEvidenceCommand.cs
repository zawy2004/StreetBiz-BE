using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.VendorRegistration;

namespace StreetBiz.Application.Features.VendorRegistration.SubmitEvidence;

/// <summary>REG-02: attach an identity/address/licence evidence document to a registration.</summary>
public sealed record SubmitEvidenceCommand(
    long RegistrationId,
    string EvidenceType,
    string FileUrl,
    string? OcrExtractedData,
    /// <summary>
    /// Separate, explicit consent to run AI-OCR on this ID photo later (WARD-04/05/06's
    /// document check). Only meaningful when true; a false/omitted value never clears an
    /// already-recorded consent. See docs_system/features/ward-review-permit-compliance.md 6.1.
    /// </summary>
    bool BiometricConsent = false) : IRequest<RegistrationEvidenceDto>;

public sealed class SubmitEvidenceCommandValidator : AbstractValidator<SubmitEvidenceCommand>
{
    public SubmitEvidenceCommandValidator()
    {
        RuleFor(x => x.EvidenceType)
            .Must(t => EvidenceTypes.All.Contains(t))
            .WithMessage(RegMessages.UploadValidDocument);

        // Only URLs issued by POST /api/uploads/evidence are accepted, so reviewers can
        // always open the document (a browser blob: or arbitrary link cannot be).
        RuleFor(x => x.FileUrl)
            .Must(url => EvidenceFiles.TryParseUrl(url, out _, out _))
            .WithMessage(RegMessages.UploadValidDocument)
            .MaximumLength(500).WithMessage(RegMessages.UploadValidDocument);
    }
}

public sealed class SubmitEvidenceCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    IFileStorage storage,
    IBusinessRegistrationRepository repository) : IRequestHandler<SubmitEvidenceCommand, RegistrationEvidenceDto>
{
    public async Task<RegistrationEvidenceDto> Handle(SubmitEvidenceCommand request, CancellationToken cancellationToken)
    {
        // Ownership guard: the registration must belong to the calling vendor.
        var registration = await vendorContext.RequireOwnedRegistrationAsync(request.RegistrationId, cancellationToken);

        // BR-62: documents can only be added while the application is still editable.
        if (!RegistrationStatuses.Editable.Contains(registration.RegistrationStatus))
        {
            throw new DomainRuleException(string.Format(RegMessages.NotEditable, RegMessages.StatusWord(registration.RegistrationStatus)));
        }

        // The file must be one this caller uploaded, not another user's document.
        EvidenceFiles.TryParseUrl(request.FileUrl, out var ownerUserId, out var fileName);
        if (ownerUserId != currentUser.UserId
            || !await storage.ExistsAsync(EvidenceFiles.StoragePath(ownerUserId, fileName), cancellationToken))
        {
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                [nameof(request.FileUrl)] = [RegMessages.UploadValidDocument],
            });
        }

        var evidence = new NewRegistrationEvidence(request.EvidenceType, request.FileUrl, request.OcrExtractedData);
        var id = await repository.AddEvidenceAsync(request.RegistrationId, evidence, cancellationToken);

        if (request.BiometricConsent)
        {
            await repository.RecordBiometricConsentAsync(request.RegistrationId, cancellationToken);
        }

        return new RegistrationEvidenceDto(
            id, request.RegistrationId, request.EvidenceType, request.FileUrl, DateTime.UtcNow);
    }
}
