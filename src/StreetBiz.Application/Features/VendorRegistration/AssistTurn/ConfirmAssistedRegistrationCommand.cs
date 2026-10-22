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
    private static readonly IReadOnlySet<string> KnownFields = new HashSet<string>(StringComparer.Ordinal)
    {
        AssistFields.BusinessLine, AssistFields.VendorType, AssistFields.LocationText, AssistFields.DeclaredAddress,
        AssistFields.CapitalAmount, AssistFields.LaborCount, AssistFields.PlannedStartDate,
        AssistFields.ContactSameAsPermanent, AssistFields.Hours, AssistFields.CartWidthMeters,
        "wardUnitId", "displayName",
    };

    public ConfirmAssistedRegistrationCommandValidator()
    {
        RuleFor(x => x.RegistrationId).GreaterThan(0);
        RuleFor(x => x.Request.Turns).InclusiveBetween(0, 200);
        RuleFor(x => x.Request.DurationSeconds).InclusiveBetween(0, 24 * 3600);
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
