using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface ICommunityVendorRepository
{
    Task<IReadOnlyList<ActiveVendorLocationRow>> SearchActiveAsync(
        CommunityVendorSearchArea area,
        int take,
        CancellationToken cancellationToken);

    Task<PublicVendorProfileRow?> GetPublicProfileAsync(
        long vendorId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PublicVendorCommentRow>> ListCommentsAsync(
        long vendorId,
        int take,
        CancellationToken cancellationToken);

    Task<PublicPermitVerificationRow?> GetPermitVerificationAsync(
        long contractId,
        string qrPayload,
        CancellationToken cancellationToken);

    Task AddPermitScanAsync(PublicPermitScan scan, CancellationToken cancellationToken);

    Task<PublicVendorCommentRow> UpsertCommentAsync(
        long vendorId,
        long customerUserId,
        CustomerVendorComment comment,
        CancellationToken cancellationToken);

    Task<bool> ReportReferencesBelongToVendorAsync(
        long vendorId,
        long? slotId,
        long? permitId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inserts the report and, in the same transaction, the notices: <c>ForWard</c> to every active
    /// officer of the report's ward(s), <c>ForVendor</c> to the vendor's account.
    /// </summary>
    Task<long> AddReportAsync(
        long vendorId,
        long customerUserId,
        CustomerVendorReport report,
        NewVendorReportNotices notices,
        CancellationToken cancellationToken);

    /// <summary>The vendor a report may be filed against, whatever its permit's state; null if unknown.</summary>
    Task<ReportTargetRow?> GetReportTargetAsync(
        long vendorId,
        long? scannedPermitId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The vendor's dish and the price-history row in force at <paramref name="atUtc"/>; null when the
    /// dish is not on one of this vendor's storefronts.
    /// </summary>
    Task<PostedPriceRow?> FindPostedPriceAsync(
        long vendorId,
        long menuItemId,
        DateTime atUtc,
        CancellationToken cancellationToken);

    Task<bool> HasPendingReportAsync(
        long reporterUserId,
        long vendorId,
        CancellationToken cancellationToken);

    /// <summary>The price board of the storefront on this contract, if it has one (BUY-06, BR-39: live).</summary>
    Task<PriceBoardRow?> GetPriceBoardByContractAsync(
        long contractId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MyVendorReportRow>> ListMyReportsAsync(
        long reporterUserId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
