using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>ADM-07: the export history (ReportExports) and the row sets behind each report type.</summary>
public sealed class PlatformReportRepository(StreetBizDbContext db, TimeProvider clock)
    : IPlatformReportRepository
{
    private const int FailureReasonLength = 500;
    private static readonly string[] ManageableRoles = [RoleCodes.Customer, RoleCodes.Vendor];

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<long> CreateExportAsync(
        long actorUserId,
        string reportType,
        DateOnly from,
        DateOnly to,
        string format,
        CancellationToken cancellationToken)
    {
        var export = new ReportExport
        {
            requested_by = actorUserId,
            report_type = reportType,
            parameters = JsonSerializer.Serialize(new ExportParameters(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), format, null)),
            export_status = ExportStatuses.Pending,
            requested_at = Now,
        };
        db.ReportExports.Add(export);
        await db.SaveChangesAsync(cancellationToken);
        return export.export_id;
    }

    public async Task CompleteExportAsync(
        long exportId,
        string fileUrl,
        string fileName,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var export = await db.ReportExports.SingleAsync(row => row.export_id == exportId, cancellationToken);
        var parameters = ReadParameters(export.parameters);
        export.export_status = ExportStatuses.Ready;
        export.file_url = fileUrl;
        export.completed_at = Now;
        export.parameters = JsonSerializer.Serialize(parameters with { FileName = fileName });
        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actorUserId,
            action = PlatformReportLimits.AuditAction,
            entity_type = PlatformReportLimits.AuditEntityType,
            entity_id = exportId,
            details = JsonSerializer.Serialize(new { export.report_type, parameters.From, parameters.To, parameters.Format }),
            created_at = Now,
        });
        // The history row and its audit entry are one SaveChanges: both exist or neither does.
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task FailExportAsync(long exportId, string reason, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var export = await db.ReportExports.SingleOrDefaultAsync(row => row.export_id == exportId, cancellationToken);
        if (export is null || export.export_status != ExportStatuses.Pending)
        {
            return;
        }

        export.export_status = ExportStatuses.Failed;
        export.failure_reason = reason.Length > FailureReasonLength ? reason[..FailureReasonLength] : reason;
        export.completed_at = Now;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PlatformPage<PlatformExportRow>> ListExportsAsync(
        long actorUserId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.ReportExports.AsNoTracking().Where(row => row.requested_by == actorUserId);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(row => row.requested_at)
            .ThenByDescending(row => row.export_id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PlatformPage<PlatformExportRow>(rows.Select(ToRow).ToArray(), page, pageSize, total);
    }

    public async Task<PlatformExportRow?> GetExportAsync(
        long exportId,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var row = await db.ReportExports.AsNoTracking()
            .SingleOrDefaultAsync(export => export.export_id == exportId && export.requested_by == actorUserId, cancellationToken);
        return row is null ? null : ToRow(row);
    }

    public Task<PlatformReportDataset> GetDatasetAsync(
        string reportType,
        DashboardRange range,
        CancellationToken cancellationToken)
    {
        var from = BusinessCalendar.StartOfDayUtc(range.From);
        var to = BusinessCalendar.EndOfDayUtc(range.To);
        var period = $"{range.From:dd/MM/yyyy} – {range.To:dd/MM/yyyy}";
        return reportType switch
        {
            PlatformReportTypes.Orders => OrdersAsync(period, from, to, cancellationToken),
            PlatformReportTypes.Moderation => ModerationAsync(period, from, to, cancellationToken),
            PlatformReportTypes.Accounts => AccountsAsync(period, from, to, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(reportType), reportType, "Unsupported report type."),
        };
    }

    private async Task<PlatformReportDataset> OrdersAsync(string period, DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = await db.Orders.AsNoTracking()
            .Where(order => order.created_at >= from && order.created_at < to)
            .OrderBy(order => order.created_at).ThenBy(order => order.order_id)
            .Take(PlatformReportLimits.MaxRows + 1)
            .Select(order => new
            {
                order.order_code,
                Storefront = order.storefront.storefront_name,
                order.order_status,
                order.subtotal_amount,
                order.total_amount,
                order.created_at,
                order.completed_at,
            })
            .ToListAsync(ct);
        return Capped(
            $"Đơn hàng {period}",
            "Đơn hàng",
            ["Mã đơn", "Gian hàng", "Trạng thái", "Tạm tính (đ)", "Tổng tiền (đ)", "Tạo lúc", "Hoàn tất lúc"],
            rows.Select(row => Row(row.order_code, row.Storefront, row.order_status, row.subtotal_amount,
                row.total_amount, Local(row.created_at), Local(row.completed_at))));
    }

    private async Task<PlatformReportDataset> ModerationAsync(string period, DateTime from, DateTime to, CancellationToken ct)
    {
        var reports = await db.ReportedContents.AsNoTracking()
            .Where(report => report.created_at >= from && report.created_at < to)
            .OrderBy(report => report.created_at).ThenBy(report => report.content_report_id)
            .Take(PlatformReportLimits.MaxRows + 1)
            .Select(report => new { report.content_report_id, report.content_type, report.content_id, report.reason, report.status, report.created_at, report.reviewed_at })
            .ToListAsync(ct);
        var complaints = await db.Complaints.AsNoTracking()
            .Where(complaint => complaint.created_at >= from && complaint.created_at < to)
            .OrderBy(complaint => complaint.created_at).ThenBy(complaint => complaint.complaint_id)
            .Take(PlatformReportLimits.MaxRows + 1)
            .Select(complaint => new { complaint.complaint_id, OrderCode = complaint.order.order_code, complaint.complaint_type, complaint.description, complaint.requested_refund_amount, complaint.status, complaint.created_at, complaint.resolved_at })
            .ToListAsync(ct);

        var truncated = reports.Count > PlatformReportLimits.MaxRows || complaints.Count > PlatformReportLimits.MaxRows;
        return new PlatformReportDataset(
            $"Kiểm duyệt {period}",
            [
                new ReportSheet(
                    "Nội dung bị báo cáo",
                    ["Mã báo cáo", "Loại nội dung", "Mã nội dung", "Lý do", "Trạng thái", "Báo cáo lúc", "Xử lý lúc"],
                    reports.Take(PlatformReportLimits.MaxRows).Select(row => Row(row.content_report_id, row.content_type,
                        row.content_id, row.reason, row.status, Local(row.created_at), Local(row.reviewed_at))).ToArray()),
                new ReportSheet(
                    "Khiếu nại đơn hàng",
                    ["Mã khiếu nại", "Mã đơn", "Loại", "Nội dung", "Yêu cầu hoàn (đ)", "Trạng thái", "Gửi lúc", "Xử lý lúc"],
                    complaints.Take(PlatformReportLimits.MaxRows).Select(row => Row(row.complaint_id, row.OrderCode,
                        row.complaint_type, row.description, row.requested_refund_amount, row.status,
                        Local(row.created_at), Local(row.resolved_at))).ToArray()),
            ],
            truncated);
    }

    private async Task<PlatformReportDataset> AccountsAsync(string period, DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = await db.UserAccounts.AsNoTracking()
            .Where(user => ManageableRoles.Contains(user.role_code) && user.created_at >= from && user.created_at < to)
            .OrderBy(user => user.created_at).ThenBy(user => user.user_id)
            .Take(PlatformReportLimits.MaxRows + 1)
            .Select(user => new
            {
                user.user_id,
                user.full_name,
                user.phone_number,
                user.role_code,
                user.account_status,
                user.created_at,
                LastLogin = user.UserSessions.Max(session => (DateTime?)session.created_at),
            })
            .ToListAsync(ct);
        return Capped(
            $"Tài khoản tạo mới {period}",
            "Tài khoản",
            ["Mã", "Họ tên", "Số điện thoại", "Vai trò", "Trạng thái", "Tạo lúc", "Đăng nhập gần nhất"],
            rows.Select(row => Row(row.user_id, row.full_name, PhoneMask.Mask(row.phone_number), row.role_code,
                row.account_status, Local(row.created_at), Local(row.LastLogin))));
    }

    private static PlatformReportDataset Capped(
        string title,
        string sheetName,
        string[] headers,
        IEnumerable<IReadOnlyList<object?>> rows)
    {
        var all = rows.ToArray();
        return new PlatformReportDataset(
            title,
            [new ReportSheet(sheetName, headers, all.Take(PlatformReportLimits.MaxRows).ToArray())],
            all.Length > PlatformReportLimits.MaxRows);
    }

    private static IReadOnlyList<object?> Row(params object?[] cells) => cells;

    /// <summary>Instants in a report are read in Da Nang time, like everywhere else on the platform.</summary>
    private static string? Local(DateTime? utc) =>
        utc is null
            ? null
            : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), BusinessCalendar.TimeZone)
                .ToString("dd/MM/yyyy HH:mm");

    private static PlatformExportRow ToRow(ReportExport row)
    {
        var parameters = ReadParameters(row.parameters);
        return new PlatformExportRow(
            row.export_id,
            row.report_type,
            DateOnly.TryParse(parameters.From, out var from) ? from : null,
            DateOnly.TryParse(parameters.To, out var to) ? to : null,
            parameters.Format,
            row.export_status,
            row.failure_reason,
            parameters.FileName,
            row.file_url,
            row.requested_at,
            row.completed_at);
    }

    private static ExportParameters ReadParameters(string? json)
    {
        try
        {
            return json is null
                ? new ExportParameters(null, null, PlatformReportFormats.Xlsx, null)
                : JsonSerializer.Deserialize<ExportParameters>(json)
                    ?? new ExportParameters(null, null, PlatformReportFormats.Xlsx, null);
        }
        catch (JsonException)
        {
            return new ExportParameters(null, null, PlatformReportFormats.Xlsx, null);
        }
    }

    private sealed record ExportParameters(string? From, string? To, string Format, string? FileName);
}
