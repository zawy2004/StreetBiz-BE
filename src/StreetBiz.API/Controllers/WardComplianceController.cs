using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using StreetBiz.Application.Features.WardCompliance;

namespace StreetBiz.API.Controllers;

/// <summary>
/// Ward Review, Permit &amp; Compliance (WARD-04..08, 11..13). Role/ward
/// enforcement happens inside each handler via IWardActorContext (re-reads the
/// caller from the database), not via [Authorize(Roles=...)] here -- same
/// pattern as WardReviewsController for WARD-16/17/18.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ward")]
public sealed class WardComplianceController(ISender sender) : ControllerBase
{
    #region Enrollments
    [HttpGet("enrollments")]
    public async Task<ActionResult<IReadOnlyList<WardEnrollmentListItemDto>>> ListEnrollments(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        CancellationToken ct = default) =>
        Ok(await sender.Send(new ListWardEnrollmentsQuery(status, page), ct));

    [HttpGet("enrollments/{id:long}")]
    public async Task<ActionResult<WardEnrollmentDetailDto>> GetEnrollment(
        long id,
        CancellationToken ct) =>
        Ok(await sender.Send(new GetWardEnrollmentDetailQuery(id), ct));

    [HttpPost("enrollments/{id:long}/decision")]
    public async Task<ActionResult<WardEnrollmentDetailDto>> DecideEnrollment(
        long id,
        WardEnrollmentDecision decision,
        CancellationToken ct) =>
        Ok(await sender.Send(new DecideWardEnrollmentCommand(id, decision), ct));

    /// <summary>BR-41 KYC gate: officer confirms they compared the vendor against their
    /// physical/chip CCCD. Required before a decision/decision with APPROVE succeeds.</summary>
    [HttpPost("enrollments/{id:long}/confirm-identity")]
    public async Task<ActionResult<WardEnrollmentDetailDto>> ConfirmIdentity(
        long id,
        ConfirmEnrollmentIdentity request,
        CancellationToken ct) =>
        Ok(await sender.Send(new ConfirmEnrollmentIdentityCommand(id, request), ct));
    #endregion

    #region Rental Applications / Temporary Usage Permits
    [HttpGet("rental-applications")]
    public async Task<ActionResult<IReadOnlyList<WardRentalApplicationListItemDto>>> ListRentalApplications(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        CancellationToken ct = default) =>
        Ok(await sender.Send(new ListWardRentalApplicationsQuery(status, page), ct));

    [HttpGet("rental-applications/{id:long}")]
    public async Task<ActionResult<WardRentalApplicationDetailDto>> GetRentalApplication(
        long id,
        CancellationToken ct) =>
        Ok(await sender.Send(new GetWardRentalApplicationDetailQuery(id), ct));

    [HttpPost("rental-applications/{id:long}/decision")]
    public async Task<ActionResult<WardRentalApplicationDetailDto>> DecideRentalApplication(
        long id,
        WardRentalApplicationDecision decision,
        CancellationToken ct) =>
        Ok(await sender.Send(new DecideWardRentalApplicationCommand(id, decision), ct));
    #endregion

    #region Renewal Applications (WARD-09)
    [HttpGet("renewals")]
    public async Task<ActionResult<IReadOnlyList<WardRenewalListItemDto>>> ListRenewals(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        CancellationToken ct = default) =>
        Ok(await sender.Send(new ListWardRenewalsQuery(status, page), ct));

    [HttpGet("renewals/{id:long}")]
    public async Task<ActionResult<WardRenewalDetailDto>> GetRenewal(
        long id,
        CancellationToken ct) =>
        Ok(await sender.Send(new GetWardRenewalDetailQuery(id), ct));

    [HttpPost("renewals/{id:long}/decision")]
    public async Task<ActionResult<WardRenewalDetailDto>> DecideRenewal(
        long id,
        WardRenewalDecision decision,
        CancellationToken ct) =>
        Ok(await sender.Send(new DecideWardRenewalCommand(id, decision), ct));

    [HttpPost("renewals/batch-decision")]
    public async Task<ActionResult<WardRenewalBatchDecisionResult>> BatchDecideRenewals(
        WardRenewalBatchDecisionRequest request,
        CancellationToken ct) =>
        Ok(await sender.Send(new BatchDecideWardRenewalsCommand(request), ct));
    #endregion

    #region On-site Inspection & Permit Verification
    [HttpPost("permits/inspect")]
    public async Task<ActionResult<InspectWardPermitResult>> InspectPermit(
        InspectWardPermitRequest request,
        CancellationToken ct) =>
        Ok(await sender.Send(new InspectWardPermitQuery(request), ct));

    [HttpPost("permits/{id:long}/action")]
    public async Task<ActionResult<bool>> PermitAction(
        long id,
        WardPermitActionRequest request,
        CancellationToken ct) =>
        Ok(await sender.Send(new ExecuteWardPermitActionCommand(id, request), ct));
    #endregion

    #region Violations & Sanctions
    [HttpGet("penalty-schedules")]
    public async Task<ActionResult<IReadOnlyList<PenaltyScheduleItemDto>>> ListPenaltySchedules(
        CancellationToken ct) =>
        Ok(await sender.Send(new ListWardPenaltySchedulesQuery(), ct));

    [HttpGet("violations")]
    public async Task<ActionResult<IReadOnlyList<WardViolationListItemDto>>> ListViolations(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        CancellationToken ct = default) =>
        Ok(await sender.Send(new ListWardViolationsQuery(status, page), ct));

    [HttpGet("violations/{id:long}")]
    public async Task<ActionResult<WardViolationDetailDto>> GetViolation(
        long id,
        CancellationToken ct) =>
        Ok(await sender.Send(new GetWardViolationDetailQuery(id), ct));

    [HttpPost("violations")]
    public async Task<ActionResult<WardViolationDetailDto>> RecordViolation(
        RecordWardViolationRequest request,
        CancellationToken ct) =>
        Ok(await sender.Send(new RecordWardViolationCommand(request), ct));

    [HttpPost("violations/{id:long}/sanction")]
    public async Task<ActionResult<WardViolationDetailDto>> SanctionViolation(
        long id,
        SanctionWardViolationRequest request,
        CancellationToken ct) =>
        Ok(await sender.Send(new SanctionWardViolationCommand(id, request), ct));
    #endregion

    #region AI Assistance
    /// <summary>Server-authoritative: loads this registration's own evidence, checks biometric
    /// consent, then re-runs AI-OCR. Never accepts a client-supplied evidence list.</summary>
    [HttpPost("ai/document-extract")]
    public async Task<ActionResult<AiDocumentCheckResult>> AiDocumentExtract(
        GetAiDocumentExtractQuery request,
        CancellationToken ct) =>
        Ok(await sender.Send(request, ct));

    [HttpPost("ai/encroachment-check")]
    public async Task<ActionResult<AiEncroachmentResult>> AiEncroachmentCheck(
        GetAiEncroachmentCheckQuery request,
        CancellationToken ct) =>
        Ok(await sender.Send(request, ct));

    /// <summary>Authenticated only -- an earlier draft left this [AllowAnonymous], letting
    /// anyone call out to a paid LLM with no rate limit. See Program.cs for the rate-limit
    /// policy applied to this route.</summary>
    [EnableRateLimiting("VendorAssistantAi")]
    [HttpPost("ai/vendor-assistant")]
    public async Task<ActionResult<VendorAssistantResponse>> VendorAssistant(
        [FromBody] VendorAssistantRequest request,
        [FromServices] IAiComplianceService aiService,
        CancellationToken ct)
    {
        var answer = await aiService.AnswerVendorAssistantAsync(request.Question, request.Context, ct);
        return Ok(new VendorAssistantResponse(answer, true));
    }
    #endregion

    #region Insights (rule-based, no LLM)
    [HttpGet("insights/risk-queue")]
    public async Task<ActionResult<IReadOnlyList<WardRiskQueueItemDto>>> RiskQueue(
        CancellationToken ct) =>
        Ok(await sender.Send(new GetWardRiskQueueQuery(), ct));

    [HttpGet("insights/patrol-heatmap")]
    public async Task<ActionResult<IReadOnlyList<WardPatrolHeatmapPointDto>>> PatrolHeatmap(
        CancellationToken ct) =>
        Ok(await sender.Send(new GetWardPatrolHeatmapQuery(), ct));
    #endregion
}

public sealed record VendorAssistantRequest(string Question, string? Context = null);
public sealed record VendorAssistantResponse(string Answer, bool IsAiGenerated);

