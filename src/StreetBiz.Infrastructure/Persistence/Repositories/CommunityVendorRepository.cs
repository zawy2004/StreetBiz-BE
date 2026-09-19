using System.Data;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class CommunityVendorRepository(
    StreetBizDbContext dbContext,
    TimeProvider clock) : ICommunityVendorRepository
{
    public async Task<IReadOnlyList<ActiveVendorLocationRow>> SearchActiveAsync(
        CommunityVendorSearchArea area,
        int take,
        CancellationToken cancellationToken)
    {
        var query =
            from validity in dbContext.vw_PermitValidities.AsNoTracking()
            join contract in dbContext.RentalContracts.AsNoTracking()
                on validity.contract_id equals contract.contract_id
            join ratingValue in dbContext.vw_VendorRatings.AsNoTracking()
                on validity.vendor_id equals ratingValue.vendor_id into ratingValues
            from rating in ratingValues.DefaultIfEmpty()
            where validity.effective_status == PermitEffectiveStatuses.Valid
                  && contract.vendor.user.account_status == AccountStatuses.Active
                  && contract.application.registration.registration_status == RegistrationStatuses.Approved
                  && contract.application.registration.vendor_id == validity.vendor_id
            select new
            {
                VendorId = validity.vendor_id,
                RegistrationId = contract.application.registration.registration_id,
                DisplayName = contract.application.registration.display_name,
                VendorType = contract.application.registration.vendor_type,
                DeclaredAddress = contract.application.registration.declared_address,
                PermitId = validity.permit_id,
                PermitEndDate = validity.end_date,
                SlotId = validity.slot_id,
                SlotCode = contract.slot.slot_code,
                ZoneName = contract.slot.zone.zone_name,
                Latitude = contract.slot.latitude,
                Longitude = contract.slot.longitude,
                CommunityRating = rating.community_rating,
                CommunityCount = rating.community_count,
                VerifiedRating = rating.verified_rating,
                VerifiedCount = rating.verified_count,
            };
        if (area.MinLatitude.HasValue)
        {
            query = query.Where(item =>
                item.Latitude >= area.MinLatitude
                && item.Latitude <= area.MaxLatitude
                && item.Longitude >= area.MinLongitude
                && item.Longitude <= area.MaxLongitude);
        }

        return await query
            .OrderBy(item => item.VendorId)
            .ThenBy(item => item.SlotId)
            .Take(take)
            .Select(item => new ActiveVendorLocationRow(
                item.VendorId,
                item.RegistrationId,
                item.DisplayName,
                item.VendorType,
                item.DeclaredAddress,
                item.PermitId,
                item.PermitEndDate,
                item.SlotId,
                item.SlotCode,
                item.ZoneName,
                item.Latitude,
                item.Longitude,
                item.CommunityRating,
                item.CommunityCount,
                item.VerifiedRating,
                item.VerifiedCount))
            .ToListAsync(cancellationToken);
    }

    public Task<PublicVendorProfileRow?> GetPublicProfileAsync(
        long vendorId,
        CancellationToken cancellationToken) =>
        (from validity in dbContext.vw_PermitValidities.AsNoTracking()
         join contract in dbContext.RentalContracts.AsNoTracking()
             on validity.contract_id equals contract.contract_id
         join ratingValue in dbContext.vw_VendorRatings.AsNoTracking()
             on validity.vendor_id equals ratingValue.vendor_id into ratingValues
         from rating in ratingValues.DefaultIfEmpty()
         where validity.vendor_id == vendorId
               && validity.effective_status == PermitEffectiveStatuses.Valid
               && contract.vendor.user.account_status == AccountStatuses.Active
               && contract.application.registration.registration_status == RegistrationStatuses.Approved
               && contract.application.registration.vendor_id == validity.vendor_id
         orderby validity.end_date descending, validity.permit_id descending
         select new PublicVendorProfileRow(
             validity.vendor_id,
             contract.application.registration.registration_id,
             contract.application.registration.display_name,
             contract.application.registration.vendor_type,
             contract.application.registration.declared_address,
             contract.application.registration.ward_unit_id,
             contract.application.registration.AdministrativeUnit!.unit_name,
             validity.permit_id,
             validity.effective_status,
             validity.end_date,
             validity.slot_id,
             contract.slot.slot_code,
             contract.slot.zone.zone_name,
             contract.slot.latitude,
             contract.slot.longitude,
             rating.community_rating,
             rating.community_count,
             rating.verified_rating,
             rating.verified_count))
        .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<PublicVendorCommentRow>> ListCommentsAsync(
        long vendorId,
        int take,
        CancellationToken cancellationToken) =>
        await dbContext.VendorComments.AsNoTracking()
            .Where(comment => comment.vendor_id == vendorId)
            .OrderByDescending(comment => comment.created_at)
            .ThenByDescending(comment => comment.comment_id)
            .Take(take)
            .Select(comment => new PublicVendorCommentRow(
                comment.comment_id,
                comment.customer_user_id,
                comment.customer_user.full_name ?? "Khách hàng",
                comment.rating,
                comment.comment_text,
                comment.created_at))
            .ToListAsync(cancellationToken);

    public Task<PublicPermitVerificationRow?> GetPermitVerificationAsync(
        long contractId,
        string qrPayload,
        CancellationToken cancellationToken) =>
        (from validity in dbContext.vw_PermitValidities.AsNoTracking()
         join contract in dbContext.RentalContracts.AsNoTracking()
             on validity.contract_id equals contract.contract_id
         where validity.contract_id == contractId
               && validity.qr_payload == qrPayload
               && contract.vendor_id == validity.vendor_id
         select new PublicPermitVerificationRow(
             validity.permit_id,
             validity.contract_id,
             validity.vendor_id,
             contract.application.registration.display_name,
             validity.slot_id,
             contract.slot.slot_code,
             contract.slot.latitude,
             contract.slot.longitude,
             validity.start_date,
             validity.end_date,
             validity.effective_status))
        .FirstOrDefaultAsync(cancellationToken);

    public async Task AddPermitScanAsync(PublicPermitScan scan, CancellationToken cancellationToken)
    {
        dbContext.PermitScanLogs.Add(new PermitScanLog
        {
            permit_id = scan.PermitId,
            qr_payload = scan.QrPayload,
            scanned_by = scan.ScannedBy,
            scan_context = PermitScanContexts.PublicCheck,
            scan_result = scan.Result,
            latitude = scan.Latitude,
            longitude = scan.Longitude,
            photo_url = scan.PhotoUrl,
            scanned_at = clock.GetUtcNow().UtcDateTime,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<PublicVendorCommentRow> UpsertCommentAsync(
        long vendorId,
        long customerUserId,
        CustomerVendorComment comment,
        CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var entity = await dbContext.VendorComments.SingleOrDefaultAsync(
                item => item.vendor_id == vendorId && item.customer_user_id == customerUserId,
                cancellationToken);

            if (entity is null)
            {
                entity = new VendorComment
                {
                    vendor_id = vendorId,
                    customer_user_id = customerUserId,
                    created_at = clock.GetUtcNow().UtcDateTime,
                };
                dbContext.VendorComments.Add(entity);
            }

            entity.rating = comment.Rating;
            entity.comment_text = comment.CommentText;
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return await dbContext.VendorComments.AsNoTracking()
            .Where(item => item.vendor_id == vendorId && item.customer_user_id == customerUserId)
            .Select(item => new PublicVendorCommentRow(
                item.comment_id,
                item.customer_user_id,
                item.customer_user.full_name ?? "Khách hàng",
                item.rating,
                item.comment_text,
                item.created_at))
            .SingleAsync(cancellationToken);
    }

    public async Task<bool> ReportReferencesBelongToVendorAsync(
        long vendorId,
        long? slotId,
        long? permitId,
        CancellationToken cancellationToken)
    {
        if (slotId.HasValue && !await dbContext.RentalContracts.AsNoTracking()
                .AnyAsync(contract => contract.vendor_id == vendorId && contract.slot_id == slotId, cancellationToken))
        {
            return false;
        }

        return !permitId.HasValue || await dbContext.DigitalPermits.AsNoTracking()
            .AnyAsync(
                permit => permit.permit_id == permitId && permit.contract.vendor_id == vendorId,
                cancellationToken);
    }

    public async Task<long> AddReportAsync(
        long vendorId,
        long customerUserId,
        CustomerVendorReport report,
        CancellationToken cancellationToken)
    {
        var entity = new VendorReport
        {
            vendor_id = vendorId,
            reporter_user_id = customerUserId,
            slot_id = report.SlotId,
            scanned_permit_id = report.ScannedPermitId,
            report_reason = report.Reason,
            evidence_url = report.EvidenceUrl,
            report_status = VendorReportStatuses.Pending,
            created_at = clock.GetUtcNow().UtcDateTime,
        };
        dbContext.VendorReports.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity.report_id;
    }

}
