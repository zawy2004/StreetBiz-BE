using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Finance.InvoiceDocuments;
using StreetBiz.Application.Features.Finance.WardReports;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Tests;

public sealed class FinanceDocumentTests
{
    private static readonly DateTime Now = new(2026, 9, 26, 18, 0, 0, DateTimeKind.Utc); // 01:00 on 27/09 in Đà Nẵng

    [Fact]
    public async Task A_receipt_download_is_named_after_the_invoice_number()
    {
        var receipt = new InvoiceDocumentRow("HD-2026-000004", "FEE", 1_040_000m, Now, "Phường Hải Châu 1",
            "Phạm Thị Lan", null, null, "NVL-01", "Kỳ 3/3 · Tháng 10/2026", null, null, "MOMO", null, Now);
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetInvoiceDocumentAsync(4, 8, It.IsAny<CancellationToken>())).ReturnsAsync(receipt);
        var renderer = new Mock<IFinanceDocumentRenderer>();
        renderer.Setup(x => x.RenderInvoicePdf(receipt, Now)).Returns([1, 2, 3]);

        var file = await new GetInvoicePdfQueryHandler(VendorContext(), finance.Object, renderer.Object, Clock())
            .Handle(new GetInvoicePdfQuery(8), CancellationToken.None);

        file.FileName.Should().Be("HD-2026-000004.pdf");
        file.ContentType.Should().Be("application/pdf");
        file.Content.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Someone_elses_receipt_is_not_found_and_nothing_is_rendered()
    {
        var renderer = new Mock<IFinanceDocumentRenderer>();

        var act = () => new GetInvoicePdfQueryHandler(
                VendorContext(), Mock.Of<IFinanceRepository>(), renderer.Object, Clock())
            .Handle(new GetInvoicePdfQuery(8), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
        renderer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task The_export_defaults_to_this_Vietnamese_month_and_is_scoped_to_the_officers_ward()
    {
        var reports = new Mock<IWardReportRepository>();
        var september = (From: new DateOnly(2026, 9, 1), To: new DateOnly(2026, 9, 27));
        reports.Setup(x => x.GetCollectionReportAsync(10, september.From, september.To, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectionReportRow(0m, 0m, 0m, 0m, 0m, 0, []));
        reports.Setup(x => x.ListWardInvoicesAsync(10, september.From, september.To, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        reports.Setup(x => x.GetWardNameAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync("Phường Hải Châu 1");
        var collections = new Mock<IWardCollectionRepository>();
        collections.Setup(x => x.ListUnpaidFeeItemsAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        collections.Setup(x => x.GetLastDebtRemindersAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, DateTime>());
        collections.Setup(x => x.ListFeeActivityAsync(10, september.From, september.To,
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        collections.Setup(x => x.ListZonesAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new WardZoneRow(3, "Nguyễn Văn Linh", 12, 8)]);
        var renderer = new Mock<IFinanceDocumentRenderer>();
        renderer.Setup(x => x.RenderCollectionReportXlsx(It.Is<CollectionReportExport>(e =>
                e.WardName == "Phường Hải Châu 1" && e.From == september.From && e.To == september.To
                // The workbook carries the same debtors and zones as the screen.
                && e.Debtors.Count == 0 && e.Performance!.ByZone.Single().ZoneName == "Nguyễn Văn Linh")))
            .Returns([9]);

        var file = await new ExportCollectionReportQueryHandler(
                WardActorContext(10), reports.Object, collections.Object, renderer.Object, Clock())
            .Handle(new ExportCollectionReportQuery(null, null), CancellationToken.None);

        file.FileName.Should().Be("bao-cao-thu-phi_20260901-20260927.xlsx");
        file.ContentType.Should().Be(ExportCollectionReportQueryHandler.XlsxContentType);
        file.Content.Should().Equal(9);
    }

    [Fact]
    public void An_export_period_that_ends_before_it_starts_is_refused() =>
        new ExportCollectionReportQueryValidator()
            .Validate(new ExportCollectionReportQuery(new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 1)))
            .IsValid.Should().BeFalse();

    private static TimeProvider Clock()
    {
        var clock = new Mock<TimeProvider>();
        clock.Setup(x => x.GetUtcNow()).Returns(new DateTimeOffset(Now));
        return clock.Object;
    }

    private static IVendorContext VendorContext()
    {
        var context = new Mock<IVendorContext>();
        context.Setup(x => x.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(4);
        return context.Object;
    }

    private static IWardActorContext WardActorContext(int wardId)
    {
        var context = new Mock<IWardActorContext>();
        context.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WardActor(2, wardId, "Cán bộ phường"));
        return context.Object;
    }
}
