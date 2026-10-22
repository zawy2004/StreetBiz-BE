using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Features.VendorRegistration.AssistTurn;

namespace StreetBiz.API.Controllers;

/// <summary>
/// X01 "Kể chuyện là xong": the voice registration assistant. It only proposes values; the
/// vendor files the registration through the normal REG-01/REG-02 endpoints with their own tap,
/// then calls /confirm so the officer can see the draft came from the assistant (BR-41/42/46).
/// Vendor-only (enforced in the handlers via IVendorContext) and rate limited per account, as
/// every turn spends speech-to-text and LLM quota.
/// </summary>
[ApiController]
[Authorize]
[Route("api/vendor/registrations")]
[EnableRateLimiting("VendorVoiceAi")]
public sealed class VendorRegistrationAssistController(ISender sender, IConfiguration configuration) : ControllerBase
{
    private static readonly JsonSerializerOptions DraftJson = new(JsonSerializerDefaults.Web);

    /// <summary>One interview turn: an audio clip or typed text answering <c>questionKey</c>, or
    /// neither to just get the next question. <c>draft</c> is the client's AssistDraft as JSON.</summary>
    [HttpPost("assist/turn")]
    [RequestSizeLimit(3 * 1024 * 1024)]
    public async Task<ActionResult<AssistTurnResult>> Turn(
        [FromForm] string questionKey,
        [FromForm] string? draft,
        [FromForm] string? text,
        [FromForm] bool voiceConsent,
        IFormFile? audio,
        CancellationToken ct)
    {
        if (!Enabled())
        {
            return NotFound();
        }

        AssistDraft parsed;
        try
        {
            parsed = string.IsNullOrWhiteSpace(draft) ? new AssistDraft() : JsonSerializer.Deserialize<AssistDraft>(draft, DraftJson) ?? new AssistDraft();
        }
        catch (JsonException)
        {
            throw new ValidationAppException(new Dictionary<string, string[]> { ["draft"] = ["Bản nháp gửi lên không hợp lệ."] });
        }

        byte[]? bytes = null;
        if (audio is not null)
        {
            if (audio.Length > AssistRegistrationTurnCommandValidator.MaxAudioBytes)
            {
                throw new ValidationAppException(new Dictionary<string, string[]> { ["audio"] = ["Đoạn ghi âm quá dài. Hãy nói ngắn hơn 30 giây."] });
            }

            using var buffer = new MemoryStream((int)audio.Length);
            await audio.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        return Ok(await sender.Send(
            new AssistRegistrationTurnCommand(questionKey, parsed, text, bytes, audio?.ContentType, voiceConsent), ct));
    }

    /// <summary>After filing: the vendor accepted the assistant's draft (idempotent per registration).</summary>
    [HttpPost("{registrationId:long}/assist/confirm")]
    public async Task<ActionResult<AssistConfirmationDto>> Confirm(
        long registrationId, [FromBody] AssistConfirmationRequest request, CancellationToken ct)
    {
        if (!Enabled())
        {
            return NotFound();
        }

        return Ok(await sender.Send(new ConfirmAssistedRegistrationCommand(registrationId, request), ct));
    }

    private bool Enabled() => configuration.GetValue("Features:VoiceRegistration", true);
}
