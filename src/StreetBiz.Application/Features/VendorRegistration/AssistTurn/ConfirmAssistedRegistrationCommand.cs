using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.AiAssistance;

namespace StreetBiz.Application.Features.VendorRegistration.AssistTurn;

/// <summary>
/// X01: called right after the vendor's own "Đúng, nộp" filed the registration. Records that the
/// AI-drafted values were reviewed and accepted by the vendor (BR-41/46), which is also what shows
/// the ward officer "[AI] soạn từ lời kể — người bán đã xác nhận". Idempotent per registration.
/// </summary>
public sealed record ConfirmAssistedRegistrationCommand(long RegistrationId, AssistConfirmationRequest Request)
    : IRequest<AssistConfirmationDto>;

public sealed class ConfirmAssistedRegistrationCommandValidator : AbstractValidator<ConfirmAssistedRegistrationCommand>
{
    /// <summary>
    /// Field names the clients report: the registration's own fields as the wizard names them
    /// (REG-01 payload, camelCase) plus the interview-only ones. Anything else is refused so the
    /// log never carries free text.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownFields = new HashSet<string>(StringComparer.Ordinal)
    {
        // REG-01 fields (web/app new-registration-store)
        "vendorType", "displayName", "declaredAddress", "addressLatitude", "addressLongitude", "wardUnitId",
        "businessLine", "capitalAmount", "laborCount", "plannedStartDate", "contactAddress", "foodSafetyCommitment",
        "ownerDateOfBirth", "ownerGender", "ownerNationality", "ownerEthnicity", "idIssuedDate", "idIssuedPlace",
        "permanentAddress",
        // interview-only
        AssistFields.LocationText, AssistFields.ContactSameAsPermanent, AssistFields.Hours, AssistFields.CartWidthMeters,
    };

    public ConfirmAssistedRegistrationCommandValidator()
    {
        RuleFor(x => x.RegistrationId).GreaterThan(0);
        RuleFor(x => x.Request.Turns).InclusiveBetween(0, 200);
        RuleFor(x => x.Request.DurationSeconds).InclusiveBetween(0, 24 * 3600);
        RuleFor(x => x.Request.AiFields!.Count).LessThanOrEqualTo(KnownFields.Count).When(x => x.Request.AiFields is not null);
        RuleFor(x => x.Request.EditedFields!.Count).LessThanOrEqualTo(KnownFields.Count).When(x => x.Request.EditedFields is not null);
        RuleForEach(x => x.Request.AiFields).Must(f => KnownFields.Contains(f));
        RuleForEach(x => x.Request.EditedFields).Must(f => KnownFields.Contains(f));
    }
}

public sealed class ConfirmAssistedRegistrationCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    IAiAssistanceLogs aiLogs) : IRequestHandler<ConfirmAssistedRegistrationCommand, AssistConfirmationDto>
{
    public async Task<AssistConfirmationDto> Handle(ConfirmAssistedRegistrationCommand command, CancellationToken cancellationToken)
    {
        // Throws 404/403 unless the registration is the caller's own.
        await vendorContext.RequireOwnedRegistrationAsync(command.RegistrationId, cancellationToken);
        var userId = currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired);

        var existing = await aiLogs.FindLatestAsync<object>(
            AiFeatureCodes.VoiceRegistration, AiLogEntities.Registration, command.RegistrationId, null, cancellationToken);
        if (existing is { Accepted: true })
        {
            return new AssistConfirmationDto(existing.AiLogId, command.RegistrationId, existing.CreatedAt);
        }

        var request = command.Request;
        var (aiLogId, reviewedAt) = await aiLogs.RecordAcceptedByAuthorAsync(
            AiFeatureCodes.VoiceRegistration,
            AiLogEntities.Registration,
            command.RegistrationId,
            userId,
            new
            {
                request.Turns,
                aiFields = request.AiFields ?? [],
                editedFields = request.EditedFields ?? [],
                request.DurationSeconds,
            },
            cancellationToken);

        return new AssistConfirmationDto(aiLogId, command.RegistrationId, reviewedAt);
    }
}
