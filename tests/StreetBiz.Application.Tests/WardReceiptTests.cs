using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.Finance.WardReports;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Tests;

/// <summary>"Lịch sử thu": the receipts of the officer's own ward, and each one's PDF.</summary>
public sealed class WardReceiptTests
{
    private static readonly WardReceiptRow PaidByPayOs = new(
        101, "HD-2026-000101", new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc), "FEE", "Phạm Thị Lan", "0905000101",
        "NVL-01", "Phí thuê ô NVL-01 — Kỳ 2/3 · Tháng 11/2026", 10_000m, "PAYOS", "PAYOS-11001", "SUCCESS");

    private static readonly WardReceiptRow AnnulledFine = new(
        102, "HD-2026-000102", new DateTime(2026, 11, 2, 3, 0, 0, DateTimeKind.Utc), "PENALTY", "Phạm Thị Lan", "0905000101",
        "NVL-08", "Tiền phạt: Cản trở lối đi bộ (QĐ QĐXP-1)", 1_000_000m, "TREASURY", "KB-0003", "REFUNDED");

    // ------------------------------------------------------------------------------- the list

    [Fact]
    public async Task Defaults_to_the_first_of_the_month_through_today_in_Vietnam()
    {
        var reports = new Mock<IWardReportRepository>();
        reports.Setup(x => x.ListWardReceiptsAsync(It.IsAny<int>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        // 18:00 UTC on 31 October is already 1 November in Đà Nẵng.
        var clock = FixedClock(new DateTime(2026, 10, 31, 18, 0, 0, DateTimeKind.Utc));

        var dto = await Handler(reports, clock, ward: 10).Handle(new ListWardReceiptsQuery(null, null), default);

        dto.From.Should().Be(new DateOnly(2026, 11, 1));
        dto.To.Should().Be(new DateOnly(2026, 11, 1));
        reports.Verify(x => x.ListWardReceiptsAsync(10, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Fills_in_the_missing_end_of_the_period()
    {
        var reports = new Mock<IWardReportRepository>();
        reports.Setup(x => x.ListWardReceiptsAsync(It.IsAny<int>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var clock = FixedClock(new DateTime(2026, 11, 7, 3, 0, 0, DateTimeKind.Utc));

        var onlyTo = await Handler(reports, clock).Handle(new ListWardReceiptsQuery(null, new DateOnly(2026, 9, 20)), default);
        var onlyFrom = await Handler(reports, clock).Handle(new ListWardReceiptsQuery(new DateOnly(2026, 10, 15), null), default);

        onlyTo.From.Should().Be(new DateOnly(2026, 9, 1));
        onlyTo.To.Should().Be(new DateOnly(2026, 9, 20));
        onlyFrom.From.Should().Be(new DateOnly(2026, 10, 15));
        onlyFrom.To.Should().Be(new DateOnly(2026, 11, 7));
    }

    [Fact]
    public async Task Gives_each_receipt_with_how_it_was_paid_and_the_reference_to_check_it_against()
    {
        var reports = new Mock<IWardReportRepository>();
        reports.Setup(x => x.ListWardReceiptsAsync(10, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([AnnulledFine, PaidByPayOs]);

        var dto = await Handler(reports, FixedClock(new DateTime(2026, 11, 7, 3, 0, 0, DateTimeKind.Utc)), ward: 10)
            .Handle(new ListWardReceiptsQuery(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 7)), default);

        dto.Items.Select(i => i.InvoiceId).Should().Equal(102, 101);
        var fee = dto.Items[1];
        fee.Provider.Should().Be("PAYOS");
        fee.ProviderReference.Should().Be("PAYOS-11001");
        fee.PaymentStatus.Should().Be("SUCCESS");
        fee.PayerName.Should().Be("Phạm Thị Lan");
        fee.PayerPhone.Should().Be("0905000101");
        fee.Amount.Should().Be(10_000m);
        dto.Items[0].PaymentStatus.Should().Be("REFUNDED");
        dto.Items[0].Kind.Should().Be("PENALTY");
    }

    [Fact]
    public async Task Refuses_a_period_longer_than_a_year_even_when_only_one_end_is_given()
    {
        var reports = new Mock<IWardReportRepository>(MockBehavior.Strict);
        var clock = FixedClock(new DateTime(2026, 11, 7, 3, 0, 0, DateTimeKind.Utc));

        var tooLong = () => Handler(reports, clock).Handle(new ListWardReceiptsQuery(new DateOnly(2025, 1, 1), null), default);
        var backwards = () => Handler(reports, clock).Handle(new ListWardReceiptsQuery(new DateOnly(2026, 12, 1), null), default);

        await tooLong.Should().ThrowAsync<DomainRuleException>().WithMessage(WardReceiptRules.TooLong);
        await backwards.Should().ThrowAsync<DomainRuleException>().WithMessage(WardReceiptRules.FromAfterTo);
    }

    [Theory]
    [InlineData("2026-01-01", "2026-12-31", true)]
    [InlineData("2025-01-02", "2026-01-02", true)] // 366 calendar days, both ends included
    [InlineData("2025-01-01", "2026-01-02", false)]
    [InlineData("2026-11-08", "2026-11-07", false)]
    public void The_validator_accepts_at_most_a_year_in_order(string from, string to, bool valid)
    {
        var result = new ListWardReceiptsQueryValidator()
            .Validate(new ListWardReceiptsQuery(DateOnly.Parse(from), DateOnly.Parse(to)));

        result.IsValid.Should().Be(valid);
    }

    // ---------------------------------------------------------------------------------- the PDF

    [Fact]
    public async Task A_receipt_of_another_ward_is_not_found_and_nothing_is_rendered()
    {
        var reports = new Mock<IWardReportRepository>();
        reports.Setup(x => x.GetReceiptVendorAsync(10, 101, It.IsAny<CancellationToken>())).ReturnsAsync((long?)null);
        var finance = new Mock<IFinanceRepository>(MockBehavior.Strict);
        var renderer = new Mock<IFinanceDocumentRenderer>(MockBehavior.Strict);

        var act = () => PdfHandler(reports, finance, renderer, ward: 10).Handle(new GetWardReceiptPdfQuery(101), default);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_receipt_of_the_ward_is_the_households_own_pdf()
    {
        var reports = new Mock<IWardReportRepository>();
        reports.Setup(x => x.GetReceiptVendorAsync(10, 101, It.IsAny<CancellationToken>())).ReturnsAsync(7L);
        var document = new InvoiceDocumentRow(
            "HD-2026-000101", "FEE", 10_000m, PaidByPayOs.IssuedAt, "Phường Hải Châu", "Phạm Thị Lan", "0905000101",
            "Bánh mì Cô Lan", "NVL-01", "Kỳ 2/3 · Tháng 11/2026", null, null, "PAYOS", "PAYOS-11001", PaidByPayOs.IssuedAt);
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetInvoiceDocumentAsync(7, 101, It.IsAny<CancellationToken>())).ReturnsAsync(document);
        var renderer = new Mock<IFinanceDocumentRenderer>();
        renderer.Setup(x => x.RenderInvoicePdf(document, It.IsAny<DateTime>())).Returns([1, 2, 3]);

        var file = await PdfHandler(reports, finance, renderer, ward: 10).Handle(new GetWardReceiptPdfQuery(101), default);

        file.FileName.Should().Be("HD-2026-000101.pdf");
        file.ContentType.Should().Be("application/pdf");
        file.Content.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void The_pdf_query_needs_a_receipt_id()
    {
        new GetWardReceiptPdfQueryValidator().Validate(new GetWardReceiptPdfQuery(0)).IsValid.Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------- helpers

    private static ListWardReceiptsQueryHandler Handler(Mock<IWardReportRepository> reports, TimeProvider clock, int ward = 10) =>
        new(WardActorContext(ward), reports.Object, clock);

    private static GetWardReceiptPdfQueryHandler PdfHandler(
        Mock<IWardReportRepository> reports,
        Mock<IFinanceRepository> finance,
        Mock<IFinanceDocumentRenderer> renderer,
        int ward) =>
        new(WardActorContext(ward), reports.Object, finance.Object, renderer.Object,
            FixedClock(new DateTime(2026, 11, 7, 3, 0, 0, DateTimeKind.Utc)));

    private static TimeProvider FixedClock(DateTime utcNow)
    {
        var clock = new Mock<TimeProvider>();
        clock.Setup(x => x.GetUtcNow()).Returns(new DateTimeOffset(utcNow, TimeSpan.Zero));
        return clock.Object;
    }

    private static IWardActorContext WardActorContext(int wardId)
    {
        var context = new Mock<IWardActorContext>();
        context.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WardActor(2, wardId, "Cán bộ phường"));
        return context.Object;
    }
}
