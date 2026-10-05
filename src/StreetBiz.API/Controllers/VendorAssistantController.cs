using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.WardCompliance;

namespace StreetBiz.API.Controllers;

/// <summary>AIC-09: onboarding chatbot. Advisory only; it never decides anything about a registration.</summary>
[ApiController]
[Authorize]
[Route("api/vendor/assistant")]
public sealed class VendorAssistantController(ICurrentUser currentUser, IAiComplianceService aiService) : ControllerBase
{
    public const int MaxQuestionLength = 500;
    public const int MaxContextLength = 1500;

    /// <summary>
    /// Authenticated vendors only. Every call goes to a paid LLM, so it is rate limited and the
    /// caller-supplied text is length-capped before it reaches the prompt.
    /// </summary>
    [EnableRateLimiting("VendorAssistantAi")]
    [HttpPost]
    public async Task<ActionResult<VendorAssistantResponse>> Ask(
        [FromBody] VendorAssistantRequest request, CancellationToken ct)
    {
        if (currentUser.RoleCode != RoleCodes.Vendor)
        {
            throw new ForbiddenException(AppMessages.Forbidden);
        }

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            errors[nameof(request.Question)] = ["Vui lòng nhập câu hỏi."];
        }
        else if (request.Question.Length > MaxQuestionLength)
        {
            errors[nameof(request.Question)] = [$"Câu hỏi không quá {MaxQuestionLength} ký tự."];
        }

        if (request.Context is { Length: > MaxContextLength })
        {
            errors[nameof(request.Context)] = [$"Ngữ cảnh không quá {MaxContextLength} ký tự."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationAppException(errors);
        }

        var answer = await aiService.AnswerVendorAssistantAsync(request.Question.Trim(), request.Context, ct);
        return Ok(new VendorAssistantResponse(answer, true));
    }
}

public sealed record VendorAssistantRequest(string Question, string? Context = null);
public sealed record VendorAssistantResponse(string Answer, bool IsAiGenerated);
