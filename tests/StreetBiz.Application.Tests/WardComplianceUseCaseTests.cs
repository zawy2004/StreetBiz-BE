using FluentAssertions;
using FluentValidation;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Features.AiAssistance;
using StreetBiz.Application.Features.WardCompliance;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Tests;

/// <summary>
/// WARD-04..WARD-13 / AIC: input rules of every ward-compliance request, and the contract every
/// handler keeps -- resolve the ward officer first, then hand that actor to the service. A caller
/// who is not a ward officer never reaches the service at all.
/// </summary>
public sealed class WardComplianceValidatorTests
{
    private static bool Valid<T>(AbstractValidator<T> validator, T request) => validator.Validate(request).IsValid;

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-3, false)]
    public void List_queries_need_a_positive_page(int page, bool valid)
    {
        Valid(new ListWardEnrollmentsQueryValidator(), new ListWardEnrollmentsQuery(null, page)).Should().Be(valid);
        Valid(new ListWardRentalApplicationsQueryValidator(), new ListWardRentalApplicationsQuery(null, page)).Should().Be(valid);
        Valid(new ListWardRenewalsQueryValidator(), new ListWardRenewalsQuery(null, page)).Should().Be(valid);
        Valid(new ListWardViolationsQueryValidator(), new ListWardViolationsQuery(null, page)).Should().Be(valid);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(0, false)]
    public void Detail_queries_need_a_positive_id(long id, bool valid)
    {
        Valid(new GetWardEnrollmentDetailQueryValidator(), new GetWardEnrollmentDetailQuery(id)).Should().Be(valid);
        Valid(new GetWardRentalApplicationDetailQueryValidator(), new GetWardRentalApplicationDetailQuery(id)).Should().Be(valid);
        Valid(new GetWardRenewalDetailQueryValidator(), new GetWardRenewalDetailQuery(id)).Should().Be(valid);
        Valid(new GetWardViolationDetailQueryValidator(), new GetWardViolationDetailQuery(id)).Should().Be(valid);
        Valid(new GenerateViolationDocumentQueryValidator(), new GenerateViolationDocumentQuery(id)).Should().Be(valid);
        Valid(new GetAiDocumentExtractQueryValidator(), new GetAiDocumentExtractQuery(id)).Should().Be(valid);
    }

    [Theory]
    [InlineData("docx", true)]
    [InlineData("pdf", true)]
    [InlineData("PDF", false)]
    [InlineData("xlsx", false)]
    public void The_registration_form_downloads_only_as_docx_or_pdf(string format, bool valid) =>
        Valid(new GenerateEnrollmentDocumentQueryValidator(), new GenerateEnrollmentDocumentQuery(3, format)).Should().Be(valid);

    [Theory]
    [InlineData("Đã đối chiếu CCCD gắn chip", true)]
    [InlineData("", false)]
    public void Identity_confirmation_needs_a_note(string note, bool valid) =>
        Valid(new ConfirmEnrollmentIdentityCommandValidator(), new ConfirmEnrollmentIdentityCommand(3, new ConfirmEnrollmentIdentity(note))).Should().Be(valid);

    [Fact]
    public void An_identity_note_is_capped_at_500_characters() =>
        Valid(new ConfirmEnrollmentIdentityCommandValidator(), new ConfirmEnrollmentIdentityCommand(3, new ConfirmEnrollmentIdentity(new string('x', 501)))).Should().BeFalse();

    [Theory]
    [InlineData("APPROVE", "Đủ điều kiện", "PENDING", true)]
    [InlineData("REJECT", "Sát trụ cứu hỏa", "PENDING", true)]
    [InlineData("MORE_INFO", "Bổ sung", "PENDING", false)]
    [InlineData("APPROVE", "", "PENDING", false)]
    [InlineData("APPROVE", "Lý do", "", false)]
    public void Rental_and_renewal_decisions_are_approve_or_reject_with_a_reason(string decision, string reason, string expected, bool valid)
    {
        Valid(new DecideWardRentalApplicationCommandValidator(), new DecideWardRentalApplicationCommand(4, new WardRentalApplicationDecision(decision, reason, expected))).Should().Be(valid);
        Valid(new DecideWardRenewalCommandValidator(), new DecideWardRenewalCommand(4, new WardRenewalDecision(decision, reason, expected))).Should().Be(valid);
    }

    [Fact]
    public void A_decision_reason_is_capped_at_500_characters()
    {
        var reason = new string('x', 501);
        Valid(new DecideWardRentalApplicationCommandValidator(), new DecideWardRentalApplicationCommand(4, new WardRentalApplicationDecision("REJECT", reason, "PENDING"))).Should().BeFalse();
        Valid(new DecideWardRenewalCommandValidator(), new DecideWardRenewalCommand(4, new WardRenewalDecision("REJECT", reason, "PENDING"))).Should().BeFalse();
    }

    private static BatchDecideWardRenewalsCommand Batch(int count, string decision = "APPROVE", string reason = "Duyệt hàng loạt", long id = 1, string expected = "PENDING") =>
        new(new WardRenewalBatchDecisionRequest(
            Enumerable.Range(0, count).Select(i => new WardRenewalBatchDecisionItemRequest(id + i, expected)).ToList(), decision, reason));

    [Fact]
    public void A_batch_renewal_decision_holds_1_to_50_valid_items()
    {
        var validator = new BatchDecideWardRenewalsCommandValidator();

        Valid(validator, Batch(1)).Should().BeTrue();
        Valid(validator, Batch(50)).Should().BeTrue();
        Valid(validator, Batch(0)).Should().BeFalse();
        Valid(validator, Batch(51)).Should().BeFalse();
        Valid(validator, Batch(2, id: 0)).Should().BeFalse();
        Valid(validator, Batch(2, expected: "")).Should().BeFalse();
        Valid(validator, Batch(2, decision: "MORE_INFO")).Should().BeFalse();
        Valid(validator, Batch(2, reason: "")).Should().BeFalse();
    }

    [Theory]
    [InlineData("SBP1.abc", null, null, true)]
    [InlineData("SBP1.abc", 16.06, 108.21, true)]
    [InlineData("", null, null, false)]
    [InlineData("SBP1.abc", 91.0, 108.0, false)]
    [InlineData("SBP1.abc", 16.0, 181.0, false)]
    public void A_permit_scan_needs_a_code_and_sane_coordinates(string code, double? lat, double? lng, bool valid) =>
        Valid(new InspectWardPermitQueryValidator(), new InspectWardPermitQuery(new InspectWardPermitRequest(code, lat, lng, null))).Should().Be(valid);

    [Fact]
    public void A_scanned_payload_is_capped_at_500_characters() =>
        Valid(new InspectWardPermitQueryValidator(), new InspectWardPermitQuery(new InspectWardPermitRequest(new string('x', 501), null, null, null))).Should().BeFalse();

    [Theory]
    [InlineData(9, "SUSPEND", "Vi phạm nhiều lần", true)]
    [InlineData(9, "REVOKE", "Nợ phạt quá hạn", true)]
    [InlineData(9, "DELETE", "x", false)]
    [InlineData(9, "SUSPEND", "", false)]
    [InlineData(0, "SUSPEND", "x", false)]
    public void A_permit_action_is_suspend_or_revoke_with_a_reason(long permitId, string action, string reason, bool valid) =>
        Valid(new ExecuteWardPermitActionCommandValidator(), new ExecuteWardPermitActionCommand(permitId, new WardPermitActionRequest(action, reason))).Should().Be(valid);

    [Theory]
    [InlineData(null, true)]
    [InlineData("UNPAID", true)]
    [InlineData("pending_sanction, paid", true)]
    [InlineData("UNPAID,BOGUS", false)]
    public void Violation_lists_accept_only_known_stages(string? status, bool valid) =>
        Valid(new ListWardViolationsQueryValidator(), new ListWardViolationsQuery(status)).Should().Be(valid);

    private static RecordWardViolationRequest Violation(string type = "OUTSIDE_SLOT", string description = "Bày hàng tràn ra lối đi",
        bool explanationRequired = false, string? method = null, string? witnessRole = null, long? sourceReport = null) =>
        new(1, 2, 3, type, description, null, WitnessRole: witnessRole, ExplanationRequired: explanationRequired, ExplanationMethod: method, SourceReportId: sourceReport);

    [Fact]
    public void Recording_a_violation_checks_type_description_explanation_witness_and_source()
    {
        var validator = new RecordWardViolationCommandValidator();
        bool Ok(RecordWardViolationRequest r) => Valid(validator, new RecordWardViolationCommand(r));

        Ok(Violation()).Should().BeTrue();
        Ok(Violation(type: "")).Should().BeFalse();
        Ok(Violation(description: "")).Should().BeFalse();
        Ok(Violation(description: new string('x', 1001))).Should().BeFalse();
        Ok(Violation(explanationRequired: true)).Should().BeFalse();
        Ok(Violation(explanationRequired: true, method: "WRITTEN")).Should().BeTrue();
        Ok(Violation(explanationRequired: true, method: "EMAIL")).Should().BeFalse();
        Ok(Violation(method: "EMAIL")).Should().BeTrue(); // the method only matters when an explanation is required
        Ok(Violation(witnessRole: "WARD_REPRESENTATIVE")).Should().BeTrue();
        Ok(Violation(witnessRole: "NEIGHBOUR")).Should().BeFalse();
        Ok(Violation(sourceReport: 0)).Should().BeFalse();
        Ok(Violation(sourceReport: 12)).Should().BeTrue();
    }

    [Theory]
    [InlineData(4, "Tôi không cố ý", true)]
    [InlineData(4, "", false)]
    [InlineData(0, "x", false)]
    public void An_explanation_needs_content(long id, string content, bool valid) =>
        Valid(new RecordWardExplanationCommandValidator(), new RecordWardExplanationCommand(id, new RecordExplanationRequest(content))).Should().Be(valid);

    [Theory]
    [InlineData("Chủ hộ", false, null, true)]
    [InlineData(null, false, null, false)]
    [InlineData(null, true, "Không ký nhận", true)]
    [InlineData(null, true, null, false)]
    public void Delivery_names_the_recipient_or_records_the_refusal(string? to, bool refused, string? reason, bool valid) =>
        Valid(new DeliverWardViolationCommandValidator(), new DeliverWardViolationCommand(4, new DeliverViolationRequest(to, refused, reason))).Should().Be(valid);

    [Theory]
    [InlineData(2, "QĐ-01", true)]
    [InlineData(0, "QĐ-01", false)]
    [InlineData(2, "", false)]
    public void A_sanction_names_the_schedule_and_the_decision_number(int schedule, string number, bool valid) =>
        Valid(new SanctionWardViolationCommandValidator(), new SanctionWardViolationCommand(4, new SanctionWardViolationRequest(schedule, number, null))).Should().Be(valid);

    [Theory]
    [InlineData("/api/uploads/evidence/2/x.jpg", true)]
    [InlineData("", false)]
    public void An_encroachment_check_needs_a_photo(string url, bool valid) =>
        Valid(new GetAiEncroachmentCheckQueryValidator(), new GetAiEncroachmentCheckQuery(url, 2, 3)).Should().Be(valid);
}

public sealed class WardComplianceHandlerTests
{
    private static readonly WardActor Officer = new(2, 10, "Cán bộ");
    private readonly Mock<IWardActorContext> actor = new();
    private readonly Mock<IWardComplianceService> service = new(MockBehavior.Loose);
    private readonly CancellationToken ct = new CancellationTokenSource().Token;

    public WardComplianceHandlerTests() => actor.Setup(a => a.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Officer);

    [Fact]
    public async Task Enrollment_handlers_pass_the_officer_and_the_request_on()
    {
        var decision = new WardEnrollmentDecision("APPROVE", "ok", "SUBMITTED");
        var identity = new ConfirmEnrollmentIdentity("đã đối chiếu");

        await new ListWardEnrollmentsQueryHandler(actor.Object, service.Object).Handle(new ListWardEnrollmentsQuery("SUBMITTED", 2, "ITINERANT"), ct);
        await new GetWardEnrollmentDetailQueryHandler(actor.Object, service.Object).Handle(new GetWardEnrollmentDetailQuery(7), ct);
        await new GenerateEnrollmentDocumentQueryHandler(actor.Object, service.Object).Handle(new GenerateEnrollmentDocumentQuery(7, "pdf"), ct);
        await new DecideWardEnrollmentCommandHandler(actor.Object, service.Object).Handle(new DecideWardEnrollmentCommand(7, decision), ct);
        await new ConfirmEnrollmentIdentityCommandHandler(actor.Object, service.Object).Handle(new ConfirmEnrollmentIdentityCommand(7, identity), ct);
        await new ClaimWardEnrollmentCommandHandler(actor.Object, service.Object).Handle(new ClaimWardEnrollmentCommand(7), ct);
        await new CheckWardFastTrackQueryHandler(actor.Object, service.Object).Handle(new CheckWardFastTrackQuery(7), ct);
        await new GetAiDocumentExtractQueryHandler(actor.Object, service.Object).Handle(new GetAiDocumentExtractQuery(7), ct);

        service.Verify(s => s.ListEnrollmentsAsync(Officer, "SUBMITTED", 2, ct, "ITINERANT"));
        service.Verify(s => s.GetEnrollmentDetailAsync(Officer, 7, ct));
        service.Verify(s => s.GenerateEnrollmentDocumentAsync(Officer, 7, "pdf", ct));
        service.Verify(s => s.DecideEnrollmentAsync(Officer, 7, decision, ct));
        service.Verify(s => s.ConfirmIdentityAsync(Officer, 7, identity, ct));
        service.Verify(s => s.ClaimEnrollmentAsync(Officer, 7, ct));
        service.Verify(s => s.CheckFastTrackAsync(Officer, 7, ct));
        service.Verify(s => s.ReRunDocumentCheckAsync(Officer, 7, ct));
    }

    [Fact]
    public async Task Rental_and_renewal_handlers_pass_the_officer_and_the_request_on()
    {
        var rental = new WardRentalApplicationDecision("APPROVE", "ok", "PENDING");
        var renewal = new WardRenewalDecision("REJECT", "vi phạm", "PENDING");
        var batch = new WardRenewalBatchDecisionRequest([new WardRenewalBatchDecisionItemRequest(1, "PENDING")], "APPROVE", "ok");

        await new ListWardRentalApplicationsQueryHandler(actor.Object, service.Object).Handle(new ListWardRentalApplicationsQuery("PENDING", 3), ct);
        await new GetWardRentalApplicationDetailQueryHandler(actor.Object, service.Object).Handle(new GetWardRentalApplicationDetailQuery(5), ct);
        await new DecideWardRentalApplicationCommandHandler(actor.Object, service.Object).Handle(new DecideWardRentalApplicationCommand(5, rental), ct);
        await new ListWardRenewalsQueryHandler(actor.Object, service.Object).Handle(new ListWardRenewalsQuery(null, 1), ct);
        await new GetWardRenewalDetailQueryHandler(actor.Object, service.Object).Handle(new GetWardRenewalDetailQuery(6), ct);
        await new DecideWardRenewalCommandHandler(actor.Object, service.Object).Handle(new DecideWardRenewalCommand(6, renewal), ct);
        await new BatchDecideWardRenewalsCommandHandler(actor.Object, service.Object).Handle(new BatchDecideWardRenewalsCommand(batch), ct);

        service.Verify(s => s.ListRentalApplicationsAsync(Officer, "PENDING", 3, ct));
        service.Verify(s => s.GetRentalApplicationDetailAsync(Officer, 5, ct));
        service.Verify(s => s.DecideRentalApplicationAsync(Officer, 5, rental, ct));
        service.Verify(s => s.ListRenewalsAsync(Officer, null, 1, ct));
        service.Verify(s => s.GetRenewalDetailAsync(Officer, 6, ct));
        service.Verify(s => s.DecideRenewalAsync(Officer, 6, renewal, ct));
        service.Verify(s => s.BatchDecideRenewalsAsync(Officer, batch, ct));
    }

    [Fact]
    public async Task Inspection_violation_and_insight_handlers_pass_the_officer_and_the_request_on()
    {
        var scan = new InspectWardPermitRequest("SBP1.x", 16.06, 108.21, null);
        var action = new WardPermitActionRequest("SUSPEND", "vi phạm");
        var record = new RecordWardViolationRequest(1, 2, 3, "OUTSIDE_SLOT", "mô tả", null);
        var explanation = new RecordExplanationRequest("giải trình");
        var delivery = new DeliverViolationRequest("Chủ hộ", false, null);
        var sanction = new SanctionWardViolationRequest(2, "QĐ-1", null);
        var asOf = new DateOnly(2026, 11, 1);

        await new InspectWardPermitQueryHandler(actor.Object, service.Object).Handle(new InspectWardPermitQuery(scan), ct);
        await new ExecuteWardPermitActionCommandHandler(actor.Object, service.Object).Handle(new ExecuteWardPermitActionCommand(9, action), ct);
        await new ListWardPenaltySchedulesQueryHandler(actor.Object, service.Object).Handle(new ListWardPenaltySchedulesQuery(asOf), ct);
        await new ListWardViolationsQueryHandler(actor.Object, service.Object).Handle(new ListWardViolationsQuery("UNPAID", 2), ct);
        await new GetWardViolationDetailQueryHandler(actor.Object, service.Object).Handle(new GetWardViolationDetailQuery(4), ct);
        await new RecordWardViolationCommandHandler(actor.Object, service.Object).Handle(new RecordWardViolationCommand(record), ct);
        await new RecordWardExplanationCommandHandler(actor.Object, service.Object).Handle(new RecordWardExplanationCommand(4, explanation), ct);
        await new DeliverWardViolationCommandHandler(actor.Object, service.Object).Handle(new DeliverWardViolationCommand(4, delivery), ct);
        await new SanctionWardViolationCommandHandler(actor.Object, service.Object).Handle(new SanctionWardViolationCommand(4, sanction), ct);
        await new GenerateViolationDocumentQueryHandler(actor.Object, service.Object).Handle(new GenerateViolationDocumentQuery(4), ct);
        await new GetWardRiskQueueQueryHandler(actor.Object, service.Object).Handle(new GetWardRiskQueueQuery(), ct);
        await new GetWardPatrolHeatmapQueryHandler(actor.Object, service.Object).Handle(new GetWardPatrolHeatmapQuery(), ct);

        service.Verify(s => s.InspectPermitAsync(Officer, scan, ct));
        service.Verify(s => s.ExecutePermitActionAsync(Officer, 9, action, ct));
        service.Verify(s => s.ListPenaltySchedulesAsync(Officer, asOf, ct));
        service.Verify(s => s.ListViolationsAsync(Officer, "UNPAID", 2, ct));
        service.Verify(s => s.GetViolationDetailAsync(Officer, 4, ct));
        service.Verify(s => s.RecordViolationAsync(Officer, record, ct));
        service.Verify(s => s.RecordExplanationAsync(Officer, 4, explanation, ct));
        service.Verify(s => s.DeliverViolationAsync(Officer, 4, delivery, ct));
        service.Verify(s => s.SanctionViolationAsync(Officer, 4, sanction, ct));
        service.Verify(s => s.GenerateViolationDocumentAsync(Officer, 4, ct));
        service.Verify(s => s.GetRiskQueueAsync(Officer, ct));
        service.Verify(s => s.GetPatrolHeatmapAsync(Officer, ct));
    }

    [Fact]
    public async Task A_caller_who_is_not_a_ward_officer_never_reaches_the_service()
    {
        actor.Setup(a => a.RequireAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new ForbiddenException("Tài khoản hiện tại không phải cán bộ phường."));

        var calls = new Func<Task>[]
        {
            () => new GetWardEnrollmentDetailQueryHandler(actor.Object, service.Object).Handle(new GetWardEnrollmentDetailQuery(1), ct),
            () => new DecideWardEnrollmentCommandHandler(actor.Object, service.Object).Handle(new DecideWardEnrollmentCommand(1, new WardEnrollmentDecision("APPROVE", "x", "SUBMITTED")), ct),
            () => new DecideWardRentalApplicationCommandHandler(actor.Object, service.Object).Handle(new DecideWardRentalApplicationCommand(1, new WardRentalApplicationDecision("APPROVE", "x", "PENDING")), ct),
            () => new ExecuteWardPermitActionCommandHandler(actor.Object, service.Object).Handle(new ExecuteWardPermitActionCommand(1, new WardPermitActionRequest("REVOKE", "x")), ct),
            () => new SanctionWardViolationCommandHandler(actor.Object, service.Object).Handle(new SanctionWardViolationCommand(1, new SanctionWardViolationRequest(1, "QĐ", null)), ct),
            () => new GetWardRiskQueueQueryHandler(actor.Object, service.Object).Handle(new GetWardRiskQueueQuery(), ct),
        };

        foreach (var call in calls)
        {
            await call.Should().ThrowAsync<ForbiddenException>();
        }

        service.VerifyNoOtherCalls();
    }
}

/// <summary>AIC-05: the encroachment check only reads photos the platform stores itself (SSRF guard).</summary>
public sealed class AiEncroachmentCheckHandlerTests
{
    private static readonly WardActor Officer = new(2, 10, "Cán bộ");
    private readonly Mock<IWardActorContext> actor = new();
    private readonly Mock<IWardAiInsights> insights = new();

    public AiEncroachmentCheckHandlerTests() => actor.Setup(a => a.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Officer);

    private Task<AiEncroachmentResult> Run(string url, long? slotId = null) =>
        new GetAiEncroachmentCheckQueryHandler(actor.Object, insights.Object).Handle(new GetAiEncroachmentCheckQuery(url, 2, 3, slotId), default);

    [Theory]
    [InlineData("https://evil.example.com/a.jpg")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/api/uploads/evidence/abc/x.jpg")]
    [InlineData("/api/uploads/menu-images/2/0123456789abcdef0123456789abcdef.jpg")]
    public async Task A_photo_not_stored_by_the_platform_is_refused_before_any_fetch(string url)
    {
        await FluentActions.Invoking(() => Run(url)).Should().ThrowAsync<DomainRuleException>();
        insights.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("/api/uploads/evidence/2/0123456789abcdef0123456789abcdef.jpg")]
    [InlineData("data:image/jpeg;base64,/9j/4AAQ")]
    public async Task An_uploaded_evidence_photo_or_inline_image_is_analysed_for_the_officer(string url)
    {
        await Run(url, slotId: 20);

        insights.Verify(i => i.CheckEncroachmentAsync(Officer, url, 2, 3, 20, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_non_officer_is_refused_before_the_url_is_even_looked_at()
    {
        actor.Setup(a => a.RequireAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new ForbiddenException("x"));

        await FluentActions.Invoking(() => Run("/api/uploads/evidence/2/0123456789abcdef0123456789abcdef.jpg")).Should().ThrowAsync<ForbiddenException>();
        insights.VerifyNoOtherCalls();
    }
}
