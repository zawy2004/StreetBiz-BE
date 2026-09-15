using FluentValidation;
using MediatR;
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
    string? OcrExtractedData) : IRequest<RegistrationEvidenceDto>;

public sealed class SubmitEvidenceCommandValidator : AbstractValidator<SubmitEvidenceCommand>
{
    public SubmitEvidenceCommandValidator()
    {
        RuleFor(x => x.EvidenceType)
            .Must(t => EvidenceTypes.All.Contains(t))
            .WithMessage(RegMessages.UploadValidDocument);

        RuleFor(x => x.FileUrl)
            .NotEmpty().WithMessage(RegMessages.UploadValidDocument)
            .MaximumLength(500);
    }
}

public sealed class SubmitEvidenceCommandHandler(
    IVendorContext vendorContext,
    IBusinessRegistrationRepository repository) : IRequestHandler<SubmitEvidenceCommand, RegistrationEvidenceDto>
{
    public async Task<RegistrationEvidenceDto> Handle(SubmitEvidenceCommand request, CancellationToken cancellationToken)
    {
        // Ownership guard: the registration must belong to the calling vendor.
        await vendorContext.RequireOwnedRegistrationAsync(request.RegistrationId, cancellationToken);

        var evidence = new NewRegistrationEvidence(request.EvidenceType, request.FileUrl, request.OcrExtractedData);
        var id = await repository.AddEvidenceAsync(request.RegistrationId, evidence, cancellationToken);

        return new RegistrationEvidenceDto(
            id, request.RegistrationId, request.EvidenceType, request.FileUrl, DateTime.UtcNow);
    }
}
