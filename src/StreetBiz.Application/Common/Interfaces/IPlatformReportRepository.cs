using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>ADM-07: the data behind an export and the ReportExports history.</summary>
public interface IPlatformReportRepository
{
    /// <summary>Opens a PENDING history row and returns its id.</summary>
    Task<long> CreateExportAsync(
        long actorUserId,
        string reportType,
        DateOnly from,
        DateOnly to,
        string format,
        CancellationToken cancellationToken);

    /// <summary>PENDING to READY with the stored file's path, and the audit row, in one transaction.</summary>
    Task CompleteExportAsync(
        long exportId,
        string fileUrl,
        string fileName,
        long actorUserId,
        CancellationToken cancellationToken);

    Task FailExportAsync(long exportId, string reason, CancellationToken cancellationToken);

    Task<PlatformPage<PlatformExportRow>> ListExportsAsync(
        long actorUserId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Only the administrator who asked for the export can read its row.</summary>
    Task<PlatformExportRow?> GetExportAsync(
        long exportId,
        long actorUserId,
        CancellationToken cancellationToken);

    /// <summary>ORDERS, MODERATION or ACCOUNTS rows for the period, capped at <c>PlatformReportLimits.MaxRows</c> per sheet.</summary>
    Task<PlatformReportDataset> GetDatasetAsync(
        string reportType,
        DashboardRange range,
        CancellationToken cancellationToken);
}
