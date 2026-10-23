using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.PlatformAdministration;

namespace StreetBiz.Application.Features.PlatformAdministration;

/// <summary>
/// ADM-07. The file is built while the request waits (a 366-day export is a few thousand rows, not
/// a job worth a queue), kept in file storage, and recorded in ReportExports so it can be fetched
/// again from the history.
/// </summary>
public sealed record ExportPlatformReportCommand(string ReportType, DateOnly From, DateOnly To, string Format)
    : IRequest<PlatformReportFile>;

public sealed class ExportPlatformReportCommandValidator : AbstractValidator<ExportPlatformReportCommand>
{
    public ExportPlatformReportCommandValidator()
    {
        RuleFor(x => x.ReportType)
            .Must(type => type is not null && PlatformReportTypes.IsValid(type.ToUpperInvariant()))
            .WithMessage("Unsupported report type.");
        RuleFor(x => x.Format)
            .Must(format => format is not null && PlatformReportFormats.IsValid(format.ToLowerInvariant()))
            .WithMessage("Format must be xlsx or csv.");
        RuleFor(x => x.From)
            .LessThanOrEqualTo(x => x.To)
            .WithMessage("The start date must not be after the end date.");
        RuleFor(x => x)
            .Must(x => x.To.DayNumber - x.From.DayNumber + 1 <= PlatformReportLimits.MaxDays)
            .When(x => x.From <= x.To)
            .WithMessage($"The period cannot be longer than {PlatformReportLimits.MaxDays} days.");
    }
}

public sealed class ExportPlatformReportCommandHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository platform,
    IPlatformReportRepository reports,
    IPlatformReportRenderer renderer,
    IFileStorage storage) : IRequestHandler<ExportPlatformReportCommand, PlatformReportFile>
{
    public async Task<PlatformReportFile> Handle(
        ExportPlatformReportCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var type = request.ReportType.ToUpperInvariant();
        var format = request.Format.ToLowerInvariant();
        var exportId = await reports.CreateExportAsync(
            actor.UserId, type, request.From, request.To, format, cancellationToken);

        try
        {
            var dataset = await BuildDatasetAsync(type, request.From, request.To, cancellationToken);
            if (dataset.Truncated)
            {
                throw new DomainRuleException(PlatformAdministrationMessages.ExportTooManyRows);
            }

            var content = format == PlatformReportFormats.Csv
                ? renderer.RenderCsv(dataset)
                : renderer.RenderXlsx(dataset);
            var fileName = FileNameFor(type, request.From, request.To, format);
            var path = $"platform-exports/{exportId}/{fileName}";
            await using (var stream = new MemoryStream(content, writable: false))
            {
                await storage.SaveAsync(path, stream, cancellationToken);
            }

            await reports.CompleteExportAsync(exportId, path, fileName, actor.UserId, cancellationToken);
            return new PlatformReportFile(content, fileName, PlatformReportFormats.ContentType(format), exportId);
        }
        catch (DomainRuleException rule)
        {
            await reports.FailExportAsync(exportId, rule.Message, CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await reports.FailExportAsync(exportId, PlatformAdministrationMessages.ExportFailed, CancellationToken.None);
            throw;
        }
    }

    private async Task<PlatformReportDataset> BuildDatasetAsync(
        string type,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var days = to.DayNumber - from.DayNumber + 1;
        var previousTo = from.AddDays(-1);
        var range = new DashboardRange(from, to, previousTo.AddDays(-(days - 1)), previousTo);
        if (type != PlatformReportTypes.OperationsSummary)
        {
            return await reports.GetDatasetAsync(type, range, cancellationToken);
        }

        var facts = await platform.GetDashboardFactsAsync(range, cancellationToken);
        return PlatformReportSheets.Operations(
            PlatformDashboardBuilder.Build(range.From, range.To, range.PreviousFrom, range.PreviousTo, facts));
    }

    public static string FileNameFor(string type, DateOnly from, DateOnly to, string format) =>
        $"streetbiz-{type.ToLowerInvariant().Replace('_', '-')}-{from:yyyyMMdd}-{to:yyyyMMdd}.{format}";
}

public sealed record ListPlatformExportsQuery(int Page = 1, int PageSize = 20)
    : IRequest<PlatformPageDto<PlatformExportDto>>;

public sealed class ListPlatformExportsQueryValidator : AbstractValidator<ListPlatformExportsQuery>
{
    public ListPlatformExportsQueryValidator()
    {
        RuleFor(x => x.Page).InclusiveBetween(1, 10_000);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListPlatformExportsQueryHandler(
    IPlatformAdminContext actorContext,
    IPlatformReportRepository reports)
    : IRequestHandler<ListPlatformExportsQuery, PlatformPageDto<PlatformExportDto>>
{
    public async Task<PlatformPageDto<PlatformExportDto>> Handle(
        ListPlatformExportsQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var page = await reports.ListExportsAsync(actor.UserId, request.Page, request.PageSize, cancellationToken);
        return new PlatformPageDto<PlatformExportDto>(
            page.Items.Select(ToDto).ToArray(), page.Page, page.PageSize, page.TotalCount);
    }

    internal static PlatformExportDto ToDto(PlatformExportRow row) => new(
        row.ExportId, row.ReportType, row.From, row.To, row.Format, row.Status, row.FailureReason,
        row.FileName, row.RequestedAt, row.CompletedAt);
}

public sealed record DownloadPlatformExportQuery(long ExportId) : IRequest<PlatformReportFile>;

public sealed class DownloadPlatformExportQueryValidator : AbstractValidator<DownloadPlatformExportQuery>
{
    public DownloadPlatformExportQueryValidator() => RuleFor(x => x.ExportId).GreaterThan(0);
}

public sealed class DownloadPlatformExportQueryHandler(
    IPlatformAdminContext actorContext,
    IPlatformReportRepository reports,
    IFileStorage storage) : IRequestHandler<DownloadPlatformExportQuery, PlatformReportFile>
{
    public async Task<PlatformReportFile> Handle(
        DownloadPlatformExportQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        // Another administrator's export is "not found", never "forbidden": it is not theirs to learn about.
        var export = await reports.GetExportAsync(request.ExportId, actor.UserId, cancellationToken)
            ?? throw new NotFoundException(PlatformAdministrationMessages.ExportNotFound);
        if (export.Status != ExportStatuses.Ready || export.FileUrl is null || export.FileName is null)
        {
            throw new DomainRuleException(PlatformAdministrationMessages.ExportNotReady);
        }

        await using var stream = await storage.OpenReadAsync(export.FileUrl, cancellationToken)
            ?? throw new NotFoundException(PlatformAdministrationMessages.ExportFileGone);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return new PlatformReportFile(
            buffer.ToArray(), export.FileName, PlatformReportFormats.ContentType(export.Format), export.ExportId);
    }
}

/// <summary>The operations summary as tables: the same figures as the dashboard, one sheet per question.</summary>
public static class PlatformReportSheets
{
    public static PlatformReportDataset Operations(PlatformDashboardDto dashboard)
    {
        var period = $"{dashboard.From:dd/MM/yyyy} – {dashboard.To:dd/MM/yyyy}";
        var summary = new List<IReadOnlyList<object?>>
        {
            Row("Đơn hàng đặt", dashboard.Orders.Placed),
            Row("Đơn hoàn tất", dashboard.Orders.Completed),
            Row("Giá trị đơn hoàn tất (đ)", dashboard.Orders.Gmv),
            Row("Tỷ lệ huỷ / từ chối (%)", dashboard.Orders.CancelRejectRate),
            Row("Người dùng mới", dashboard.Users.NewUsers),
            Row("Báo cáo nội dung mới", dashboard.Moderation.ReportsFiled),
            Snapshot("Tổng số tài khoản (hiện tại)", dashboard.Users.Total),
            Snapshot("Tài khoản đang bị khoá (hiện tại)", dashboard.Users.Suspended),
            Snapshot("Gian hàng đang mở (hiện tại)", dashboard.Marketplace.StorefrontsOpen),
            Snapshot("Gian hàng tạm dừng (hiện tại)", dashboard.Marketplace.StorefrontsPaused),
            Snapshot("Gian hàng đã đóng (hiện tại)", dashboard.Marketplace.StorefrontsClosed),
            Snapshot("Báo cáo nội dung chờ xử lý (hiện tại)", dashboard.Moderation.PendingReports),
            Snapshot("Khiếu nại đơn hàng đang mở (hiện tại)", dashboard.Moderation.OpenComplaints),
            Snapshot("Khoản hoàn tiền chờ cổng thanh toán (hiện tại)", dashboard.Moderation.PendingRefunds),
        };

        return new PlatformReportDataset(
            $"Báo cáo vận hành nền tảng {period}",
            [
                new ReportSheet(
                    "Tổng hợp",
                    ["Chỉ tiêu", "Kỳ này", "Kỳ trước", "Thay đổi (%)"],
                    summary),
                new ReportSheet(
                    "Theo ngày",
                    ["Ngày", "Đơn đặt", "Đơn hoàn tất", "Giá trị hoàn tất (đ)"],
                    dashboard.OrdersByDay
                        .Select(day => (IReadOnlyList<object?>)[$"{day.Day:dd/MM/yyyy}", day.OrdersPlaced, day.OrdersCompleted, day.Gmv])
                        .ToArray()),
                new ReportSheet(
                    "Đơn theo trạng thái",
                    ["Trạng thái", "Số đơn"],
                    dashboard.Orders.ByStatus
                        .Select(status => (IReadOnlyList<object?>)[status.Status, status.Count])
                        .ToArray()),
                new ReportSheet(
                    "Top gian hàng",
                    ["Hạng", "Gian hàng", "Đơn hoàn tất", "Doanh thu (đ)"],
                    dashboard.TopStorefronts
                        .Select((row, index) => (IReadOnlyList<object?>)[index + 1, row.StorefrontName, row.CompletedOrders, row.Revenue])
                        .ToArray()),
            ]);
    }

    private static IReadOnlyList<object?> Row(string label, DashboardMetricDto metric) =>
        [label, metric.Value, metric.Previous, metric.ChangePercent];

    private static IReadOnlyList<object?> Snapshot(string label, decimal value) => [label, value, null, null];
}
