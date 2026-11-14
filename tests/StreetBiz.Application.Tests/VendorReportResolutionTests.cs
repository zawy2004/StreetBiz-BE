using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.AiMarketplace;
using StreetBiz.Application.Features.VendorReports;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Tests;

/// <summary>
/// WARD-19: closing a citizen report is a ward officer's decision with a reason (BR-41/43), never a
/// platform administrator's (BR-44), and the inbox shows what is overdue first.
/// </summary>
public sealed class VendorReportResolutionTests
{
    private const int Ward = 4;
    private static readonly DateTime Now = new(2026, 10, 23, 5, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IWardActorContext> actors = new();
    private readonly Mock<IVendorReportDecisions> decisions = new();
    private readonly Mock<IAiMarketplaceRepository> repository = new();
    private readonly Mock<IVendorReportAssistant> assistant = new();

    public VendorReportResolutionTests()
    {
        actors.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new WardActor(9, Ward, "Cán bộ"));
        assistant.Setup(x => x.ReadAsync(It.IsAny<VendorReportContextRow>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VendorReportAssistDto(false, null, null, null, null, [], null, null, null, "[AI] chỉ để tham khảo"));
    }

    [Theory]
    [InlineData(VendorReportResolutions.Dismissed, "Không đủ căn cứ: giấy phép hợp lệ.", null, true)]
    [InlineData(VendorReportResolutions.Mediated, "Người bán đã hoàn tiền.", 30_000.0, true)]
    [InlineData(VendorReportResolutions.Mediated, "Người bán đã hoàn tiền.", 0.0, true)]
    [InlineData(VendorReportResolutions.Mediated, "   ", null, false)]
    [InlineData(VendorReportResolutions.Dismissed, "", null, false)]
    [InlineData(VendorReportResolutions.Dismissed, "Bác", 10_000.0, false)]
    [InlineData(VendorReportResolutions.Mediated, "Hoàn tiền", -1.0, false)]
    [InlineData(VendorReportResolutions.Mediated, "Hoàn tiền", 10_000.5, false)]
    [InlineData(VendorReportResolutions.ViolationRecorded, "Biên bản", null, false)]
    [InlineData("AUTO_CLOSED", "x", null, false)]
    public void A_resolution_needs_an_outcome_a_note_and_at_most_a_whole_refund_on_mediation(
        string outcome, string note, double? refund, bool valid) =>
        new ResolveWardVendorReportCommandValidator()
            .Validate(new ResolveWardVendorReportCommand(1, new ResolveVendorReportRequest(
                VendorReportStatuses.Pending, outcome, note, (decimal?)refund)))
            .IsValid.Should().Be(valid);

    [Fact]
    public void The_note_is_capped_and_the_expected_status_is_required()
    {
        var validator = new ResolveWardVendorReportCommandValidator();
        validator.Validate(new ResolveWardVendorReportCommand(1, new ResolveVendorReportRequest(
            VendorReportStatuses.Pending, VendorReportResolutions.Dismissed, new string('x', 501)))).IsValid.Should().BeFalse();
        validator.Validate(new ResolveWardVendorReportCommand(1, new ResolveVendorReportRequest(
            "", VendorReportResolutions.Dismissed, "Lý do"))).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_platform_administrator_cannot_close_a_report()
    {
        actors.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ForbiddenException("Tài khoản hiện tại không phải cán bộ phường."));

        var act = () => Handler().Handle(new ResolveWardVendorReportCommand(1, new ResolveVendorReportRequest(
            VendorReportStatuses.Pending, VendorReportResolutions.Dismissed, "Lý do")), default);

        await act.Should().ThrowAsync<ForbiddenException>();
        decisions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task The_officer_decides_with_a_trimmed_note_and_gets_the_closed_report_back()
    {
        repository.Setup(x => x.GetVendorReportAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Row(VendorReportStatuses.Dismissed, outcome: new VendorReportOutcomeRow(
                VendorReportResolutions.Dismissed, "Lý do", null, Now, "Cán bộ")));

        var result = await Handler().Handle(new ResolveWardVendorReportCommand(1, new ResolveVendorReportRequest(
            VendorReportStatuses.Pending, VendorReportResolutions.Dismissed, "  Lý do  ")), default);

        decisions.Verify(x => x.ResolveAsync(It.Is<WardActor>(a => a.WardId == Ward), 1,
            It.Is<ResolveVendorReportRequest>(r => r.Note == "Lý do"), It.IsAny<CancellationToken>()));
        result.Status.Should().Be(VendorReportStatuses.Dismissed);
        result.Outcome!.Resolution.Should().Be(VendorReportResolutions.Dismissed);
        result.Outcome.ReviewedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task The_detail_carries_the_price_evidence_the_due_time_and_a_rule_based_violation_type()
    {
        var evidence = new VendorReportPriceEvidenceRow(12, "Bún chả", "suất", 45_000, 3, Now.AddDays(-20), 2, 90_000, 120_000, Now.AddDays(-1));
        repository.Setup(x => x.GetVendorReportAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Row(VendorReportStatuses.Pending, VendorReportTypes.Overcharge, Now.AddHours(-1), evidence, reportsIn90Days: 3));

        var result = await new GetWardVendorReportQueryHandler(actors.Object, repository.Object, assistant.Object, new FixedClock(Now))
            .Handle(new GetWardVendorReportQuery(1), default);

        result.ReportType.Should().Be(VendorReportTypes.Overcharge);
        result.IsOverdue.Should().BeTrue();
        result.SuggestedViolationType.Should().Be("PRICE_ABOVE_POSTED");
        result.PriceEvidence!.Difference.Should().Be(30_000);
        result.PriceEvidence.PostedAmount.Should().Be(90_000);
        result.ReportsAgainstVendor90Days.Should().Be(3);
        result.VendorId.Should().Be(3);
        // The existing AIB-01 notes still travel with the report, for the screen to label [AI] (BR-42).
        result.Assist.Should().NotBeNull();
        assistant.Verify(x => x.ReadAsync(It.Is<VendorReportContextRow>(r => r.ReportId == 1), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Another_wards_report_is_not_found()
    {
        repository.Setup(x => x.GetVendorReportAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Row(VendorReportStatuses.Pending, wards: [Ward + 1]));

        var act = () => new GetWardVendorReportQueryHandler(actors.Object, repository.Object, assistant.Object)
            .Handle(new GetWardVendorReportQuery(1), default);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task The_list_passes_its_filters_and_marks_overdue_reports()
    {
        repository.Setup(x => x.ListVendorReportsForWardAsync(Ward,
                It.Is<VendorReportListFilter>(f => f.Type == VendorReportTypes.Overcharge && f.Overdue == true && f.NowUtc == Now),
                1, ListWardVendorReportsQueryHandler.PageSize, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Row(VendorReportStatuses.Pending, VendorReportTypes.Overcharge, Now.AddHours(-2))]);

        var list = await new ListWardVendorReportsQueryHandler(actors.Object, repository.Object, new FixedClock(Now))
            .Handle(new ListWardVendorReportsQuery(null, 1, VendorReportTypes.Overcharge, true), default);

        list.Should().ContainSingle().Which.IsOverdue.Should().BeTrue();
        list[0].ReportType.Should().Be(VendorReportTypes.Overcharge);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(VendorReportTypes.Overcharge, true)]
    [InlineData(VendorReportTypes.PriceNotPosted, true)]
    [InlineData("SCORE", false)]
    public void The_list_type_filter_is_validated(string? type, bool valid) =>
        new ListWardVendorReportsQueryValidator().Validate(new ListWardVendorReportsQuery(null, 1, type)).IsValid.Should().Be(valid);

    private ResolveWardVendorReportCommandHandler Handler() =>
        new(actors.Object, decisions.Object, repository.Object, assistant.Object, new FixedClock(Now));

    private static VendorReportContextRow Row(
        string status,
        string type = VendorReportTypes.General,
        DateTime? dueAt = null,
        VendorReportPriceEvidenceRow? evidence = null,
        int reportsIn90Days = 0,
        VendorReportOutcomeRow? outcome = null,
        int[]? wards = null) =>
        new(1, "Bị tính cao hơn giá niêm yết", null, status, Now.AddDays(-8), null, "Bún chả Cô Lan", 3, 100, "A-01",
            null, null, null, null, null, null, null, wards ?? [Ward], type, dueAt, outcome, evidence, reportsIn90Days);

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
