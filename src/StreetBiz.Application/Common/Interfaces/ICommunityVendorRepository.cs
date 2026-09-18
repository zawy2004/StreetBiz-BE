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

    Task<long> AddReportAsync(
        long vendorId,
        long customerUserId,
        CustomerVendorReport report,
        CancellationToken cancellationToken);
}
