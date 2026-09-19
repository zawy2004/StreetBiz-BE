using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class PlatformAdministrationRepository(
    StreetBizDbContext db,
    TimeProvider clock) : IPlatformAdministrationRepository
{
    private const string MenuItemHidden = "HIDDEN";
    private const string StorefrontPaused = "PAUSED";
    private const string PaymentSuccess = "SUCCESS";
    private const string RefundPending = "PENDING";
    private const string RefundFailed = "FAILED";
    private const string RefundReasonComplaint = "COMPLAINT_RESOLVED";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<FoodCategoryRow>> ListFoodCategoriesAsync(
        CancellationToken cancellationToken) =>
        await CategoryQuery()
            .ToListAsync(cancellationToken);

    public Task<bool> FoodCategoryNameExistsAsync(
        string name,
        int? exceptCategoryId,
        CancellationToken cancellationToken) =>
        db.FoodCategories.AsNoTracking().AnyAsync(
            category => category.category_name == name
                && (!exceptCategoryId.HasValue || category.category_id != exceptCategoryId),
            cancellationToken);

    public async Task<FoodCategoryRow?> CreateFoodCategoryAsync(
        string name,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var categoryId = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            if (await db.FoodCategories.AnyAsync(
                    category => category.category_name == name,
                    cancellationToken))
            {
                return (int?)null;
            }

            var entity = new FoodCategory { category_name = name, created_by = actorUserId };
            db.FoodCategories.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            Audit(actorUserId, "ADM_CATEGORY_CREATE", "FoodCategory", entity.category_id, new { name });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return entity.category_id;
        });

        return categoryId.HasValue
            ? await CategoryQuery(categoryId).SingleAsync(cancellationToken)
            : null;
    }

    public async Task<FoodCategoryRow?> RenameFoodCategoryAsync(
        int categoryId,
        string name,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var updated = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var category = await db.FoodCategories.SingleOrDefaultAsync(
                row => row.category_id == categoryId, cancellationToken);
            if (category is null)
            {
                return false;
            }

            if (await db.FoodCategories.AnyAsync(
                    row => row.category_id != categoryId && row.category_name == name,
                    cancellationToken))
            {
                return false;
            }

            var previousName = category.category_name;
            category.category_name = name;
            Audit(actorUserId, "ADM_CATEGORY_RENAME", "FoodCategory", categoryId,
                new { previousName, name });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        });

        return updated
            ? await CategoryQuery(categoryId).SingleAsync(cancellationToken)
            : null;
    }

    public async Task<CategoryDeleteOutcome> DeleteFoodCategoryAsync(
        int categoryId,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var category = await db.FoodCategories.SingleOrDefaultAsync(
                row => row.category_id == categoryId, cancellationToken);
            if (category is null)
            {
                return CategoryDeleteOutcome.NotFound;
            }

            if (await db.MenuItems.AnyAsync(item => item.category_id == categoryId, cancellationToken))
            {
                return CategoryDeleteOutcome.InUse;
            }

            Audit(actorUserId, "ADM_CATEGORY_DELETE", "FoodCategory", categoryId,
                new { name = category.category_name });
            db.FoodCategories.Remove(category);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return CategoryDeleteOutcome.Deleted;
        });
    }

    public async Task<PlatformPage<ReportedContentRow>> ListReportedContentAsync(
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.ReportedContents.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(report => report.status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        var records = await query
            .OrderByDescending(report => report.created_at)
            .ThenByDescending(report => report.content_report_id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(report => new ReportedContentData(
                report.content_report_id,
                report.content_type,
                report.content_id,
                report.reporter_user.full_name ?? $"User #{report.reporter_user_id}",
                report.reason,
                report.status,
                report.reviewed_by.HasValue ? report.UserAccount!.full_name : null,
                report.created_at,
                report.reviewed_at))
            .ToListAsync(cancellationToken);
        return new PlatformPage<ReportedContentRow>(
            await EnrichReportedContentAsync(records, cancellationToken),
            page,
            pageSize,
            total);
    }

    public async Task<ReportedContentRow?> GetReportedContentAsync(
        long reportId,
        CancellationToken cancellationToken)
    {
        var record = await db.ReportedContents.AsNoTracking()
            .Where(report => report.content_report_id == reportId)
            .Select(report => new ReportedContentData(
                report.content_report_id,
                report.content_type,
                report.content_id,
                report.reporter_user.full_name ?? $"User #{report.reporter_user_id}",
                report.reason,
                report.status,
                report.reviewed_by.HasValue ? report.UserAccount!.full_name : null,
                report.created_at,
                report.reviewed_at))
            .SingleOrDefaultAsync(cancellationToken);
        if (record is null)
        {
            return null;
        }

        return (await EnrichReportedContentAsync([record], cancellationToken))[0];
    }

    public async Task<ContentDecisionResult> DecideReportedContentAsync(
        long reportId,
        string expectedStatus,
        string decision,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var outcome = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var report = await db.ReportedContents.SingleOrDefaultAsync(
                row => row.content_report_id == reportId, cancellationToken);
            if (report is null)
            {
                return ContentDecisionOutcome.NotFound;
            }

            if (report.status != expectedStatus || report.status != ReportedContentStatuses.Pending)
            {
                return ContentDecisionOutcome.Conflict;
            }

            long notificationUserId;
            if (decision == ContentModerationDecisions.Hide)
            {
                var contentOwner = await HideContentAsync(
                    report.content_type, report.content_id, cancellationToken);
                if (!contentOwner.HasValue)
                {
                    return ContentDecisionOutcome.ContentNotFound;
                }

                notificationUserId = contentOwner.Value;
                var duplicates = await db.ReportedContents
                    .Where(row => row.content_type == report.content_type
                        && row.content_id == report.content_id
                        && row.status == ReportedContentStatuses.Pending)
                    .ToListAsync(cancellationToken);
                foreach (var duplicate in duplicates)
                {
                    duplicate.status = ReportedContentStatuses.Hidden;
                    duplicate.reviewed_by = actorUserId;
                    duplicate.reviewed_at = Now;
                }
            }
            else
            {
                notificationUserId = report.reporter_user_id;
                report.status = ReportedContentStatuses.Dismissed;
                report.reviewed_by = actorUserId;
                report.reviewed_at = Now;
            }

            AddNotification(
                notificationUserId,
                "CONTENT_MODERATION",
                decision == ContentModerationDecisions.Hide
                    ? "Nội dung đã bị ẩn"
                    : "Báo cáo nội dung đã được xem xét",
                decision == ContentModerationDecisions.Hide
                    ? "Nội dung vi phạm đã bị quản trị viên ẩn khỏi nền tảng."
                    : "Báo cáo không đủ căn cứ và đã được đóng.",
                "ReportedContent",
                reportId);
            Audit(actorUserId, $"ADM_CONTENT_{decision}", "ReportedContent", reportId,
                new { report.content_type, report.content_id, expectedStatus });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ContentDecisionOutcome.Updated;
        });

        var row = outcome == ContentDecisionOutcome.Updated
            ? await GetReportedContentAsync(reportId, cancellationToken)
            : null;
        return new ContentDecisionResult(outcome, row);
    }

    public async Task<PlatformPage<OrderComplaintRow>> ListOrderComplaintsAsync(
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Complaints.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(complaint => complaint.status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        var records = await query
            .OrderByDescending(complaint => complaint.created_at)
            .ThenByDescending(complaint => complaint.complaint_id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(complaint => new ComplaintData(
                complaint.complaint_id,
                complaint.order_id,
                complaint.order.order_code,
                complaint.order.order_status,
                complaint.customer_user.full_name ?? $"User #{complaint.customer_user_id}",
                complaint.order.storefront.storefront_name,
                complaint.complaint_type,
                complaint.description,
                complaint.requested_refund_amount,
                complaint.status,
                complaint.resolution_notes,
                complaint.resolved_by.HasValue ? complaint.UserAccount!.full_name : null,
                complaint.created_at,
                complaint.resolved_at))
            .ToListAsync(cancellationToken);
        return new PlatformPage<OrderComplaintRow>(
            await EnrichComplaintsAsync(records, cancellationToken),
            page,
            pageSize,
            total);
    }

    public async Task<OrderComplaintRow?> GetOrderComplaintAsync(
        long complaintId,
        CancellationToken cancellationToken)
    {
        var record = await db.Complaints.AsNoTracking()
            .Where(complaint => complaint.complaint_id == complaintId)
            .Select(complaint => new ComplaintData(
                complaint.complaint_id,
                complaint.order_id,
                complaint.order.order_code,
                complaint.order.order_status,
                complaint.customer_user.full_name ?? $"User #{complaint.customer_user_id}",
                complaint.order.storefront.storefront_name,
                complaint.complaint_type,
                complaint.description,
                complaint.requested_refund_amount,
                complaint.status,
                complaint.resolution_notes,
                complaint.resolved_by.HasValue ? complaint.UserAccount!.full_name : null,
                complaint.created_at,
                complaint.resolved_at))
            .SingleOrDefaultAsync(cancellationToken);
        if (record is null)
        {
            return null;
        }

        return (await EnrichComplaintsAsync([record], cancellationToken))[0];
    }

    public async Task<ComplaintDecisionResult> DecideOrderComplaintAsync(
        long complaintId,
        ComplaintResolution resolution,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var outcome = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var complaint = await db.Complaints.SingleOrDefaultAsync(
                row => row.complaint_id == complaintId, cancellationToken);
            if (complaint is null)
            {
                return ComplaintDecisionOutcome.NotFound;
            }

            if (complaint.status != resolution.ExpectedStatus
                || complaint.status is not (ComplaintStatuses.Open or ComplaintStatuses.UnderReview))
            {
                return ComplaintDecisionOutcome.Conflict;
            }

            if (resolution.ApprovedRefundAmount.HasValue)
            {
                if (resolution.Decision != ComplaintDecisions.Resolve
                    || complaint.complaint_type != ComplaintTypes.RefundRequest
                    || !complaint.requested_refund_amount.HasValue)
                {
                    return ComplaintDecisionOutcome.RefundNotAllowed;
                }

                var amount = resolution.ApprovedRefundAmount.Value;
                if (amount > complaint.requested_refund_amount.Value)
                {
                    return ComplaintDecisionOutcome.RefundExceedsLimit;
                }

                var payment = await db.PaymentTransactions
                    .Where(row => row.order_id == complaint.order_id
                        && row.payment_purpose == "ORDER"
                        && row.transaction_status == PaymentSuccess)
                    .OrderByDescending(row => row.created_at)
                    .ThenByDescending(row => row.transaction_id)
                    .FirstOrDefaultAsync(cancellationToken);
                if (payment is null)
                {
                    return ComplaintDecisionOutcome.PaymentNotFound;
                }

                var alreadyRefunded = await db.RefundTransactions
                    .Where(row => row.payment_transaction_id == payment.transaction_id
                        && row.refund_status != RefundFailed)
                    .SumAsync(row => (decimal?)row.amount, cancellationToken) ?? 0;
                if (alreadyRefunded + amount > payment.amount
                    || alreadyRefunded + amount > complaint.requested_refund_amount.Value)
                {
                    return ComplaintDecisionOutcome.RefundExceedsLimit;
                }

                db.RefundTransactions.Add(new RefundTransaction
                {
                    order_id = complaint.order_id,
                    payment_transaction_id = payment.transaction_id,
                    complaint_id = complaint.complaint_id,
                    idempotency_key = $"ADM-COMPLAINT-{complaint.complaint_id}-{Guid.NewGuid():N}",
                    amount = amount,
                    refund_reason = RefundReasonComplaint,
                    provider = payment.provider,
                    refund_status = RefundPending,
                    requested_at = Now,
                });
            }

            complaint.status = resolution.Decision == ComplaintDecisions.Resolve
                ? ComplaintStatuses.Resolved
                : ComplaintStatuses.Rejected;
            complaint.resolved_by = actorUserId;
            complaint.resolution_notes = resolution.Notes;
            complaint.resolved_at = Now;
            AddNotification(
                complaint.customer_user_id,
                "ORDER_COMPLAINT",
                "Khiếu nại đơn hàng đã được xử lý",
                resolution.ApprovedRefundAmount.HasValue
                    ? $"Khiếu nại đã được chấp thuận; yêu cầu hoàn {resolution.ApprovedRefundAmount.Value:N0}₫ đang chờ cổng thanh toán."
                    : resolution.Decision == ComplaintDecisions.Resolve
                        ? "Khiếu nại đã được giải quyết."
                        : "Khiếu nại đã bị từ chối.",
                "Complaint",
                complaintId);
            Audit(actorUserId, $"ADM_COMPLAINT_{resolution.Decision}", "Complaint", complaintId,
                new { resolution.ExpectedStatus, resolution.ApprovedRefundAmount });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ComplaintDecisionOutcome.Updated;
        });

        var row = outcome == ComplaintDecisionOutcome.Updated
            ? await GetOrderComplaintAsync(complaintId, cancellationToken)
            : null;
        return new ComplaintDecisionResult(outcome, row);
    }

    private IQueryable<FoodCategoryRow> CategoryQuery(int? categoryId = null)
    {
        var query = db.FoodCategories.AsNoTracking();
        if (categoryId.HasValue)
        {
            query = query.Where(category => category.category_id == categoryId.Value);
        }

        return query
            .OrderBy(category => category.category_name)
            .Select(category => new FoodCategoryRow(
                category.category_id,
                category.category_name,
                category.MenuItems.Count,
                category.UserAccount != null ? category.UserAccount.full_name : null));
    }

    private async Task<IReadOnlyList<ReportedContentRow>> EnrichReportedContentAsync(
        IReadOnlyList<ReportedContentData> records,
        CancellationToken cancellationToken)
    {
        var menuIds = records.Where(row => row.ContentType == ReportedContentTypes.MenuItem)
            .Select(row => row.ContentId).Distinct().ToArray();
        var storefrontIds = records.Where(row => row.ContentType == ReportedContentTypes.Storefront)
            .Select(row => row.ContentId).Distinct().ToArray();
        var reviewIds = records.Where(row => row.ContentType == ReportedContentTypes.Review)
            .Select(row => row.ContentId).Distinct().ToArray();

        var menus = await db.MenuItems.AsNoTracking()
            .Where(item => menuIds.Contains(item.menu_item_id))
            .Select(item => new ContentSnapshot(
                item.menu_item_id,
                item.item_name,
                item.description,
                item.availability_status,
                true,
                item.storefront.registration.vendor.user_id))
            .ToDictionaryAsync(item => item.ContentId, cancellationToken);
        var storefronts = await db.Storefronts.AsNoTracking()
            .Where(storefront => storefrontIds.Contains(storefront.storefront_id))
            .Select(storefront => new ContentSnapshot(
                storefront.storefront_id,
                storefront.storefront_name,
                storefront.description,
                storefront.availability_status,
                true,
                storefront.registration.vendor.user_id))
            .ToDictionaryAsync(item => item.ContentId, cancellationToken);
        var hiddenReviewIds = await db.ReportedContents.AsNoTracking()
            .Where(report => report.content_type == ReportedContentTypes.Review
                && reviewIds.Contains(report.content_id)
                && report.status == ReportedContentStatuses.Hidden)
            .Select(report => report.content_id)
            .Distinct()
            .ToListAsync(cancellationToken);
        var hiddenReviewSet = hiddenReviewIds.ToHashSet();
        var reviews = await db.Reviews.AsNoTracking()
            .Where(review => reviewIds.Contains(review.review_id))
            .Select(review => new ContentSnapshot(
                review.review_id,
                "Đánh giá đơn " + review.order.order_code,
                review.review_text,
                hiddenReviewSet.Contains(review.review_id) ? ReportedContentStatuses.Hidden : "VISIBLE",
                true,
                review.customer_user_id))
            .ToDictionaryAsync(item => item.ContentId, cancellationToken);

        return records.Select(record =>
        {
            var found = record.ContentType switch
            {
                ReportedContentTypes.MenuItem => menus.GetValueOrDefault(record.ContentId),
                ReportedContentTypes.Storefront => storefronts.GetValueOrDefault(record.ContentId),
                ReportedContentTypes.Review => reviews.GetValueOrDefault(record.ContentId),
                _ => null,
            };
            var snapshot = found ?? new ContentSnapshot(
                record.ContentId, "Nội dung không còn tồn tại", null, "MISSING", false, null);
            return new ReportedContentRow(
                record.ReportId,
                record.ContentType,
                record.ContentId,
                snapshot.Title,
                snapshot.Body,
                snapshot.Status,
                snapshot.Exists,
                record.ReporterName,
                record.Reason,
                record.Status,
                record.ReviewedByName,
                record.CreatedAt,
                record.ReviewedAt);
        }).ToArray();
    }

    private async Task<IReadOnlyList<OrderComplaintRow>> EnrichComplaintsAsync(
        IReadOnlyList<ComplaintData> records,
        CancellationToken cancellationToken)
    {
        var orderIds = records.Select(row => row.OrderId).Distinct().ToArray();
        var complaintIds = records.Select(row => row.ComplaintId).Distinct().ToArray();
        var payments = await db.PaymentTransactions.AsNoTracking()
            .Where(payment => orderIds.Contains(payment.order_id!.Value)
                && payment.payment_purpose == "ORDER"
                && payment.transaction_status == PaymentSuccess)
            .OrderByDescending(payment => payment.created_at)
            .ThenByDescending(payment => payment.transaction_id)
            .Select(payment => new PaymentSnapshot(
                payment.order_id!.Value,
                payment.amount,
                payment.provider))
            .ToListAsync(cancellationToken);
        var paymentsByOrder = payments.GroupBy(payment => payment.OrderId)
            .ToDictionary(group => group.Key, group => group.First());
        var refunds = await db.RefundTransactions.AsNoTracking()
            .Where(refund => refund.complaint_id.HasValue
                && complaintIds.Contains(refund.complaint_id.Value))
            .OrderByDescending(refund => refund.requested_at)
            .ThenByDescending(refund => refund.refund_id)
            .Select(refund => new RefundSnapshot(
                refund.complaint_id!.Value,
                refund.refund_id,
                refund.amount,
                refund.refund_status))
            .ToListAsync(cancellationToken);
        var refundsByComplaint = refunds.GroupBy(refund => refund.ComplaintId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        return records.Select(record =>
        {
            paymentsByOrder.TryGetValue(record.OrderId, out var payment);
            refundsByComplaint.TryGetValue(record.ComplaintId, out var complaintRefunds);
            var activeRefunds = complaintRefunds?.Where(refund => refund.Status != RefundFailed).ToArray()
                ?? [];
            var latest = complaintRefunds?.FirstOrDefault();
            return new OrderComplaintRow(
                record.ComplaintId,
                record.OrderId,
                record.OrderCode,
                record.OrderStatus,
                record.CustomerName,
                record.StorefrontName,
                record.ComplaintType,
                record.Description,
                record.RequestedRefundAmount,
                record.Status,
                record.ResolutionNotes,
                record.ResolvedByName,
                record.CreatedAt,
                record.ResolvedAt,
                payment?.Amount,
                payment?.Provider,
                activeRefunds.Sum(refund => refund.Amount),
                latest?.RefundId,
                latest?.Status);
        }).ToArray();
    }

    private async Task<long?> HideContentAsync(
        string contentType,
        long contentId,
        CancellationToken cancellationToken)
    {
        switch (contentType)
        {
            case ReportedContentTypes.MenuItem:
                var menuItem = await db.MenuItems.SingleOrDefaultAsync(
                    item => item.menu_item_id == contentId, cancellationToken);
                if (menuItem is null)
                {
                    return null;
                }

                menuItem.availability_status = MenuItemHidden;
                menuItem.updated_at = Now;
                return await db.MenuItems.Where(item => item.menu_item_id == contentId)
                    .Select(item => (long?)item.storefront.registration.vendor.user_id)
                    .SingleAsync(cancellationToken);

            case ReportedContentTypes.Storefront:
                var storefront = await db.Storefronts.SingleOrDefaultAsync(
                    item => item.storefront_id == contentId, cancellationToken);
                if (storefront is null)
                {
                    return null;
                }

                storefront.availability_status = StorefrontPaused;
                storefront.updated_at = Now;
                return await db.Storefronts.Where(item => item.storefront_id == contentId)
                    .Select(item => (long?)item.registration.vendor.user_id)
                    .SingleAsync(cancellationToken);

            case ReportedContentTypes.Review:
                return await db.Reviews.Where(review => review.review_id == contentId)
                    .Select(review => (long?)review.customer_user_id)
                    .SingleOrDefaultAsync(cancellationToken);

            default:
                return null;
        }
    }

    private void Audit(long actorUserId, string action, string entityType, long entityId, object details) =>
        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actorUserId,
            action = action,
            entity_type = entityType,
            entity_id = entityId,
            details = JsonSerializer.Serialize(details),
            created_at = Now,
        });

    private void AddNotification(
        long userId,
        string type,
        string title,
        string body,
        string entityType,
        long entityId) =>
        db.Notifications.Add(new Notification
        {
            user_id = userId,
            notification_type = type,
            title = title,
            body = body,
            related_entity_type = entityType,
            related_entity_id = entityId,
            is_read = false,
            sent_at = Now,
        });

    private sealed record ReportedContentData(
        long ReportId,
        string ContentType,
        long ContentId,
        string ReporterName,
        string Reason,
        string Status,
        string? ReviewedByName,
        DateTime CreatedAt,
        DateTime? ReviewedAt);

    private sealed record ContentSnapshot(
        long ContentId,
        string Title,
        string? Body,
        string Status,
        bool Exists,
        long? OwnerUserId);

    private sealed record ComplaintData(
        long ComplaintId,
        long OrderId,
        string OrderCode,
        string OrderStatus,
        string CustomerName,
        string StorefrontName,
        string ComplaintType,
        string Description,
        decimal? RequestedRefundAmount,
        string Status,
        string? ResolutionNotes,
        string? ResolvedByName,
        DateTime CreatedAt,
        DateTime? ResolvedAt);

    private sealed record PaymentSnapshot(long OrderId, decimal Amount, string Provider);
    private sealed record RefundSnapshot(long ComplaintId, long RefundId, decimal Amount, string Status);
}
