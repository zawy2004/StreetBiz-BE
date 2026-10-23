using System.Data;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
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
        NewVendorReportNotices notices,
        CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                dbContext.ChangeTracker.Clear();
                await using var transaction = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
                var now = clock.GetUtcNow().UtcDateTime;
                var entity = new VendorReport
                {
                    vendor_id = vendorId,
                    reporter_user_id = customerUserId,
                    slot_id = report.SlotId,
                    scanned_permit_id = report.ScannedPermitId,
                    report_reason = report.Reason,
                    evidence_url = report.EvidenceUrl,
                    report_status = VendorReportStatuses.Pending,
                    report_type = report.ReportType,
                    menu_item_id = report.MenuItemId,
                    price_history_id = report.PriceHistoryId,
                    quantity = report.Quantity,
                    posted_amount = report.PostedAmount,
                    charged_amount = report.ChargedAmount,
                    purchased_at = report.PurchasedAt,
                    due_at = report.DueAt,
                    created_at = now,
                };
                dbContext.VendorReports.Add(entity);
                await dbContext.SaveChangesAsync(cancellationToken);

                // The officers who will see it in their inbox, by the same routes the inbox uses.
                var wards = await VendorReportRecords.WardIdsAsync(
                    dbContext, vendorId, report.SlotId, report.ScannedPermitId, cancellationToken);
                var officers = await dbContext.UserAccounts.AsNoTracking()
                    .Where(user => user.role_code == RoleCodes.WardAuthority
                        && user.account_status == AccountStatuses.Active
                        && user.ward_unit_id != null
                        && wards.Contains(user.ward_unit_id.Value))
                    .Select(user => user.user_id)
                    .ToListAsync(cancellationToken);
                foreach (var officer in officers)
                {
                    VendorReportRecords.Notify(dbContext, officer, notices.ForWard, entity.report_id, now,
                        VendorReportNotificationTypes.New);
                }

                if (await dbContext.Vendors.AsNoTracking().Where(vendor => vendor.vendor_id == vendorId)
                        .Select(vendor => (long?)vendor.user_id).SingleOrDefaultAsync(cancellationToken) is { } vendorUserId)
                {
                    VendorReportRecords.Notify(dbContext, vendorUserId, notices.ForVendor, entity.report_id, now,
                        VendorReportNotificationTypes.AgainstYou);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return entity.report_id;
            });
        }
        catch (DbUpdateException)
        {
            // Two submissions racing past the handler's check: UQ_VendorReports_OnePendingPerReporter
            // lets one in. The other gets the same answer the check gives.
            dbContext.ChangeTracker.Clear();
            if (await HasPendingReportAsync(customerUserId, vendorId, cancellationToken))
            {
                throw new ConflictException(CommunityMessages.DuplicatePendingReport);
            }

            throw;
        }
    }

    public async Task<ReportTargetRow?> GetReportTargetAsync(
        long vendorId,
        long? scannedPermitId,
        CancellationToken cancellationToken)
    {
        if (!await dbContext.Vendors.AsNoTracking().AnyAsync(vendor => vendor.vendor_id == vendorId, cancellationToken))
        {
            return null;
        }

        var (name, slotCode) = await VendorReportRecords.SubjectAsync(
            dbContext,
            new VendorReport { vendor_id = vendorId, scanned_permit_id = scannedPermitId },
            cancellationToken);
        // No registration means no ward to send the report to.
        return name is null ? null : new ReportTargetRow(vendorId, name, slotCode);
    }

    public async Task<PostedPriceRow?> FindPostedPriceAsync(
        long vendorId,
        long menuItemId,
        DateTime atUtc,
        CancellationToken cancellationToken)
    {
        var item = await dbContext.MenuItems.AsNoTracking()
            .Where(row => row.menu_item_id == menuItemId && row.storefront.registration.vendor_id == vendorId)
            .Select(row => new
            {
                row.menu_item_id,
                row.storefront_id,
                row.item_name,
                row.storefront.storefront_name,
                row.storefront.contract.slot.slot_code,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            return null;
        }

        // The row in force at the purchase: the last one written at or before it. Later rows (a price
        // raised after the sale) cannot change what was posted then.
        var posted = await dbContext.MenuItemPriceHistories.AsNoTracking()
            .Where(row => row.menu_item_id == menuItemId && row.changed_at <= atUtc)
            .OrderByDescending(row => row.changed_at)
            .ThenByDescending(row => row.price_history_id)
            .FirstOrDefaultAsync(cancellationToken);
        return new PostedPriceRow(
            item.menu_item_id,
            item.storefront_id,
            item.storefront_name,
            item.slot_code,
            posted?.price_history_id,
            posted?.item_name ?? item.item_name,
            posted?.price_unit,
            posted?.unit_price,
            posted?.is_listed ?? false,
            posted?.changed_at);
    }

    public Task<bool> HasPendingReportAsync(
        long reporterUserId,
        long vendorId,
        CancellationToken cancellationToken) =>
        dbContext.VendorReports.AsNoTracking().AnyAsync(
            report => report.reporter_user_id == reporterUserId
                && report.vendor_id == vendorId
                && report.report_status == VendorReportStatuses.Pending,
            cancellationToken);

    public Task<PriceBoardRow?> GetPriceBoardByContractAsync(
        long contractId,
        CancellationToken cancellationToken) =>
        PriceBoards.ReadAsync(
            dbContext,
            dbContext.Storefronts.Where(storefront => storefront.contract_id == contractId),
            cancellationToken);

    public async Task<IReadOnlyList<MyVendorReportRow>> ListMyReportsAsync(
        long reporterUserId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.VendorReports.AsNoTracking()
            .Where(report => report.reporter_user_id == reporterUserId)
            .OrderByDescending(report => report.created_at)
            .ThenByDescending(report => report.report_id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(report => new
            {
                report.report_id,
                report.vendor_id,
                StoreName = report.menu_item != null ? report.menu_item.storefront.storefront_name : null,
                PermitName = report.scanned_permit != null
                    ? report.scanned_permit.contract.application.registration.display_name
                    : null,
                VendorName = dbContext.BusinessRegistrations
                    .Where(registration => registration.vendor_id == report.vendor_id)
                    .OrderByDescending(registration => registration.registration_id)
                    .Select(registration => registration.display_name)
                    .FirstOrDefault(),
                SlotCode = report.slot != null ? report.slot.slot_code
                    : report.scanned_permit != null ? report.scanned_permit.contract.slot.slot_code
                    : report.menu_item != null ? report.menu_item.storefront.contract.slot.slot_code
                    : null,
                report.report_type,
                report.report_reason,
                report.report_status,
                report.created_at,
                report.due_at,
                report.resolution,
                report.resolution_note,
                report.refund_amount,
                report.reviewed_at,
                report.evidence_url,
                report.menu_item_id,
                report.quantity,
                report.posted_amount,
                report.charged_amount,
                report.purchased_at,
                PriceItemName = report.price_history != null ? report.price_history.item_name : null,
                PriceUnit = report.price_history != null ? report.price_history.price_unit : null,
                PriceUnitAmount = report.price_history != null ? (decimal?)report.price_history.unit_price : null,
                PriceChangedAt = report.price_history != null ? (DateTime?)report.price_history.changed_at : null,
                PriceVersion = report.price_history != null
                    ? dbContext.MenuItemPriceHistories.Count(row =>
                        row.storefront_id == report.price_history.storefront_id
                        && row.price_history_id <= report.price_history.price_history_id)
                    : 0,
            })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new MyVendorReportRow(
                row.report_id,
                row.vendor_id,
                row.StoreName ?? row.PermitName ?? row.VendorName,
                row.SlotCode,
                row.report_type,
                row.report_reason,
                row.report_status,
                row.created_at,
                row.due_at,
                row.resolution,
                row.resolution_note,
                row.refund_amount,
                row.reviewed_at,
                !string.IsNullOrWhiteSpace(row.evidence_url),
                row.menu_item_id is { } item && row.PriceUnitAmount is { } unitPrice && row.quantity is { } quantity
                    && row.posted_amount is { } posted && row.charged_amount is { } charged && row.purchased_at is { } purchased
                    ? new VendorReportPriceEvidenceRow(
                        item, row.PriceItemName!, row.PriceUnit!, unitPrice, row.PriceVersion, row.PriceChangedAt!.Value,
                        quantity, posted, charged, purchased)
                    : null))
            .ToArray();
    }
}
