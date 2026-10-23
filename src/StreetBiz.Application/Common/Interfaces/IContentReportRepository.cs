using StreetBiz.Application.Features.Commerce;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>REV-03: files a report into the ADM-03 review queue (the ReportedContent table).</summary>
public interface IContentReportRepository
{
    /// <summary>
    /// Checks that the content exists and is not hidden, is not the reporter's own, is not already
    /// reported by them and still pending, and that they are under the hourly limit, then inserts
    /// the PENDING report and its audit row, all in one serializable transaction.
    /// </summary>
    Task<ContentReportResult> CreateAsync(ContentReportDraft draft, CancellationToken cancellationToken);
}
