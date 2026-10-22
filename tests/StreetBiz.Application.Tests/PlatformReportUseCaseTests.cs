using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.PlatformAdministration;

namespace StreetBiz.Application.Tests;

/// <summary>ADM-07: validation, the export flow and who may download what.</summary>
public sealed class PlatformReportUseCaseTests
{
    private static readonly DateOnly From = new(2026, 10, 1);
    private static readonly DateOnly To = new(2026, 10, 3);
    private static readonly byte[] Bytes = [1, 2, 3];

    private readonly Mock<IPlatformAdministrationRepository> platform = new();
    private readonly Mock<IPlatformReportRepository> reports = new();
    private readonly Mock<IPlatformReportRenderer> renderer = new();
    private readonly Mock<IFileStorage> storage = new();

    private static Mock<IPlatformAdminContext> Admin(long id = 7)
    {
        var actor = new Mock<IPlatformAdminContext>();
        actor.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PlatformAdminActor(id, "Admin"));
        return actor;
    }

    private ExportPlatformReportCommandHandler Handler() =>
        new(Admin().Object, platform.Object, reports.Object, renderer.Object, storage.Object);

    private static PlatformReportDataset Dataset(bool truncated = false) =>
        new("t", [new ReportSheet("s", ["h"], [])], truncated);

    public PlatformReportUseCaseTests()
    {
        reports.Setup(x => x.CreateExportAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateOnly>(),
            It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(55);
        reports.Setup(x => x.GetDatasetAsync(It.IsAny<string>(), It.IsAny<DashboardRange>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Dataset());
        renderer.Setup(x => x.RenderXlsx(It.IsAny<PlatformReportDataset>())).Returns(Bytes);
        renderer.Setup(x => x.RenderCsv(It.IsAny<PlatformReportDataset>())).Returns(Bytes);
    }

    [Theory]
    [InlineData("ORDERS", "xlsx", 1, 3, true)]
    [InlineData("orders", "CSV", 1, 3, true)]
    [InlineData("NOPE", "xlsx", 1, 3, false)]
    [InlineData("ORDERS", "pdf", 1, 3, false)]
    [InlineData("ORDERS", "xlsx", 3, 1, false)]
    public void The_request_needs_a_known_type_format_and_ordered_dates(
        string type, string format, int fromDay, int toDay, bool valid) =>
        new ExportPlatformReportCommandValidator()
            .Validate(new ExportPlatformReportCommand(type, new DateOnly(2026, 10, fromDay), new DateOnly(2026, 10, toDay), format))
            .IsValid.Should().Be(valid);

    [Fact]
    public void The_period_is_limited_to_366_days()
    {
        var validator = new ExportPlatformReportCommandValidator();

        validator.Validate(new ExportPlatformReportCommand("ORDERS", From, From.AddDays(365), "xlsx")).IsValid.Should().BeTrue();
        validator.Validate(new ExportPlatformReportCommand("ORDERS", From, From.AddDays(366), "xlsx")).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_good_export_is_rendered_stored_recorded_and_returned()
    {
        var file = await Handler().Handle(new ExportPlatformReportCommand("orders", From, To, "XLSX"), default);

        file.Content.Should().Equal(Bytes);
        file.FileName.Should().Be("streetbiz-orders-20261001-20261003.xlsx");
        file.ContentType.Should().Be(PlatformReportFormats.XlsxContentType);
        file.ExportId.Should().Be(55);
        storage.Verify(x => x.SaveAsync("platform-exports/55/streetbiz-orders-20261001-20261003.xlsx",
            It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
        reports.Verify(x => x.CompleteExportAsync(55, "platform-exports/55/streetbiz-orders-20261001-20261003.xlsx",
            "streetbiz-orders-20261001-20261003.xlsx", 7, It.IsAny<CancellationToken>()), Times.Once);
        reports.Verify(x => x.FailExportAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_csv_request_uses_the_csv_renderer()
    {
        var file = await Handler().Handle(new ExportPlatformReportCommand("ACCOUNTS", From, To, "csv"), default);

        file.ContentType.Should().Be(PlatformReportFormats.CsvContentType);
        file.FileName.Should().EndWith(".csv");
        renderer.Verify(x => x.RenderCsv(It.IsAny<PlatformReportDataset>()), Times.Once);
        renderer.Verify(x => x.RenderXlsx(It.IsAny<PlatformReportDataset>()), Times.Never);
    }

    [Fact]
    public async Task The_operations_summary_is_built_from_the_dashboard_figures()
    {
        platform.Setup(x => x.GetDashboardFactsAsync(It.IsAny<DashboardRange>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformDashboardFacts(
                new DashboardPeriodRow(0, 0, 0, 0, 0, 0m, 0, 0, null),
                new DashboardPeriodRow(0, 0, 0, 0, 0, 0m, 0, 0, null),
                new DashboardSnapshotRow(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0m), [], [], []));
        PlatformReportDataset? rendered = null;
        renderer.Setup(x => x.RenderXlsx(It.IsAny<PlatformReportDataset>()))
            .Callback<PlatformReportDataset>(d => rendered = d).Returns(Bytes);

        await Handler().Handle(new ExportPlatformReportCommand("OPERATIONS_SUMMARY", From, To, "xlsx"), default);

        rendered!.Sheets.Select(s => s.Name).Should().Equal("Tổng hợp", "Theo ngày", "Đơn theo trạng thái", "Top gian hàng");
        rendered.Sheets[1].Rows.Should().HaveCount(3);
        reports.Verify(x => x.GetDatasetAsync(It.IsAny<string>(), It.IsAny<DashboardRange>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Too_many_rows_marks_the_export_failed_and_stores_nothing()
    {
        reports.Setup(x => x.GetDatasetAsync(It.IsAny<string>(), It.IsAny<DashboardRange>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Dataset(truncated: true));

        var act = () => Handler().Handle(new ExportPlatformReportCommand("ORDERS", From, To, "xlsx"), default);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage(PlatformAdministrationMessages.ExportTooManyRows);
        reports.Verify(x => x.FailExportAsync(55, PlatformAdministrationMessages.ExportTooManyRows, It.IsAny<CancellationToken>()), Times.Once);
        storage.Verify(x => x.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_unexpected_failure_is_recorded_without_leaking_its_details()
    {
        renderer.Setup(x => x.RenderXlsx(It.IsAny<PlatformReportDataset>())).Throws(new InvalidOperationException("secret path C:\\x"));

        var act = () => Handler().Handle(new ExportPlatformReportCommand("ORDERS", From, To, "xlsx"), default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        reports.Verify(x => x.FailExportAsync(55, PlatformAdministrationMessages.ExportFailed, It.IsAny<CancellationToken>()), Times.Once);
        reports.Verify(x => x.CompleteExportAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_non_admin_cannot_export_or_download()
    {
        var denied = new Mock<IPlatformAdminContext>();
        denied.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new ForbiddenException("no"));

        var export = () => new ExportPlatformReportCommandHandler(denied.Object, platform.Object, reports.Object, renderer.Object, storage.Object)
            .Handle(new ExportPlatformReportCommand("ORDERS", From, To, "xlsx"), default);
        var download = () => new DownloadPlatformExportQueryHandler(denied.Object, reports.Object, storage.Object)
            .Handle(new DownloadPlatformExportQuery(1), default);

        await export.Should().ThrowAsync<ForbiddenException>();
        await download.Should().ThrowAsync<ForbiddenException>();
        reports.Verify(x => x.CreateExportAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateOnly>(),
            It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static PlatformExportRow Export(string status, string? url = "platform-exports/9/f.csv") => new(
        9, "ORDERS", From, To, "csv", status, null, "f.csv", url, DateTime.UtcNow, DateTime.UtcNow);

    private DownloadPlatformExportQueryHandler Download() => new(Admin().Object, reports.Object, storage.Object);

    [Fact]
    public async Task Downloading_streams_the_stored_file_with_its_own_type()
    {
        reports.Setup(x => x.GetExportAsync(9, 7, It.IsAny<CancellationToken>())).ReturnsAsync(Export(ExportStatuses.Ready));
        storage.Setup(x => x.OpenReadAsync("platform-exports/9/f.csv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(Bytes));

        var file = await Download().Handle(new DownloadPlatformExportQuery(9), default);

        file.Content.Should().Equal(Bytes);
        file.FileName.Should().Be("f.csv");
        file.ContentType.Should().Be(PlatformReportFormats.CsvContentType);
    }

    [Fact]
    public async Task Somebody_elses_export_looks_like_it_does_not_exist()
    {
        reports.Setup(x => x.GetExportAsync(9, 7, It.IsAny<CancellationToken>())).ReturnsAsync((PlatformExportRow?)null);

        var act = () => Download().Handle(new DownloadPlatformExportQuery(9), default);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Theory]
    [InlineData(ExportStatuses.Pending)]
    [InlineData(ExportStatuses.Failed)]
    public async Task An_export_that_is_not_ready_cannot_be_downloaded(string status)
    {
        reports.Setup(x => x.GetExportAsync(9, 7, It.IsAny<CancellationToken>())).ReturnsAsync(Export(status, null));

        var act = () => Download().Handle(new DownloadPlatformExportQuery(9), default);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage(PlatformAdministrationMessages.ExportNotReady);
    }

    [Fact]
    public async Task A_file_missing_from_storage_is_reported_as_gone()
    {
        reports.Setup(x => x.GetExportAsync(9, 7, It.IsAny<CancellationToken>())).ReturnsAsync(Export(ExportStatuses.Ready));
        storage.Setup(x => x.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Stream?)null);

        var act = () => Download().Handle(new DownloadPlatformExportQuery(9), default);

        await act.Should().ThrowAsync<NotFoundException>().WithMessage(PlatformAdministrationMessages.ExportFileGone);
    }
}
