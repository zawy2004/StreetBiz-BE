using System.Data;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.FoodSafety;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Services;

public sealed class CommerceManagement(StreetBizDbContext db, IVendorContext vendors,
    ICustomerContext customers, IFileStorage storage, TimeProvider clock) : ICommerceManagement
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(Now.AddHours(7));

    public async Task<IReadOnlyList<StorefrontDto>> Stores(CancellationToken ct)
    {
        var vendor = await vendors.RequireVendorIdAsync(ct);
        return await db.Storefronts.AsNoTracking().Where(x => x.registration.vendor_id == vendor)
            .OrderBy(x => x.storefront_id).Select(x => new StorefrontDto(x.storefront_id,
                x.registration_id, x.contract_id, x.storefront_name, x.description, x.availability_status)).ToListAsync(ct);
    }

    public async Task<StorefrontDto> SaveStore(long? id, StorefrontInput input, CancellationToken ct)
    {
        var vendor = await vendors.RequireVendorIdAsync(ct);
        Text(input.Name, 180, "Tên gian hàng");
        OptionalText(input.Description, 1000);
        if (input.AvailabilityStatus is not ("OPEN" or "PAUSED" or "CLOSED"))
            throw new DomainRuleException("Trạng thái gian hàng không hợp lệ.");
        var storeId = await Write(async () =>
        {
            var store = id.HasValue ? await OwnedStore(id.Value, vendor, ct) : new Storefront();
            if (id.HasValue && (store.registration_id != input.RegistrationId || store.contract_id != input.ContractId))
                throw new ConflictException("Không thể đổi hồ sơ hoặc hợp đồng của gian hàng.");
            // A moderator hides a storefront by pausing it and marking the report HIDDEN; the report
            // is the record that the seller may not reopen it, whatever status the store shows now.
            if (id.HasValue &&
                await db.ReportedContents.AnyAsync(x => x.content_type == "STOREFRONT" &&
                    x.content_id == id.Value && x.status == "HIDDEN", ct))
                throw new ForbiddenException("Gian hàng đã bị quản trị viên ẩn.");
            // The SQL Phase2Gate applies to every storefront update, including closing.
            // Validate it here so an expired/cancelled contract returns a domain error.
            var eligible = await db.RentalContracts.AnyAsync(x => x.contract_id == input.ContractId &&
                x.vendor_id == vendor && x.contract_status == "ACTIVE" && x.start_date <= Today && x.end_date >= Today &&
                x.application.registration_id == input.RegistrationId && x.application.registration.vendor_id == vendor &&
                x.application.registration.registration_status == "APPROVED", ct);
            if (!eligible) throw new DomainRuleException("Cần hồ sơ đã duyệt và hợp đồng đang hiệu lực của chính bạn.");
            if (!id.HasValue)
            {
                if (await db.Storefronts.AnyAsync(x => x.contract_id == input.ContractId || x.registration_id == input.RegistrationId, ct))
                    throw new ConflictException("Hồ sơ hoặc hợp đồng này đã có gian hàng.");
                store.registration_id = input.RegistrationId;
                store.contract_id = input.ContractId;
                store.created_at = Now;
                db.Storefronts.Add(store);
            }
            store.storefront_name = input.Name.Trim();
            store.description = input.Description?.Trim();
            store.availability_status = input.AvailabilityStatus;
            store.updated_at = Now;
            await db.SaveChangesAsync(ct);
            return store.storefront_id;
        }, ct);
        return (await Stores(ct)).Single(x => x.StorefrontId == storeId);
    }

    public async Task<StorefrontHoursDto> BusinessHours(long storeId, CancellationToken ct)
    {
        var vendor = await vendors.RequireVendorIdAsync(ct);
        return await ReadHours(storeId, vendor, ct);
    }

    public async Task<StorefrontHoursDto> SaveBusinessHours(long storeId, StorefrontHoursInput input, CancellationToken ct)
    {
        var vendor = await vendors.RequireVendorIdAsync(ct);
        await Write(async () =>
        {
            var store = await OwnedStore(storeId, vendor, ct);
            var (from, to) = await RentalWindow(store.contract_id, ct);
            // Validated against the zone's hours inside the transaction, so a ward changing them
            // between the editor opening and saving cannot let a stale window through.
            var rows = StorefrontHoursRules.Validate(input.Hours, from, to);

            var existing = await db.StorefrontBusinessHours.Where(x => x.storefront_id == storeId).ToListAsync(ct);
            db.StorefrontBusinessHours.RemoveRange(existing);
            db.StorefrontBusinessHours.AddRange(rows.Select(row => new StorefrontBusinessHour
            {
                storefront_id = storeId,
                day_of_week = row.DayOfWeek,
                opens_at = row.OpensAt,
                closes_at = row.ClosesAt,
            }));
            var ownerUserId = await db.Vendors.Where(x => x.vendor_id == vendor).Select(x => x.user_id).SingleAsync(ct);
            db.AuditLogs.Add(new AuditLog
            {
                actor_user_id = ownerUserId,
                action = "STORE_HOURS_UPDATE",
                entity_type = "Storefront",
                entity_id = storeId,
                details = System.Text.Json.JsonSerializer.Serialize(new { before = existing.Count, after = rows.Count }),
                created_at = Now,
            });
            store.updated_at = Now;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return await ReadHours(storeId, vendor, ct);
    }

    private async Task<StorefrontHoursDto> ReadHours(long storeId, long vendor, CancellationToken ct)
    {
        var store = await db.Storefronts.AsNoTracking()
            .Where(x => x.storefront_id == storeId && x.registration.vendor_id == vendor)
            .Select(x => new { x.availability_status, x.contract_id })
            .SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Không tìm thấy gian hàng của bạn.");
        var rows = await db.StorefrontBusinessHours.AsNoTracking().Where(x => x.storefront_id == storeId)
            .OrderBy(x => x.day_of_week).ThenBy(x => x.opens_at)
            .Select(x => new StorefrontHourRow(x.day_of_week, x.opens_at, x.closes_at)).ToListAsync(ct);
        var (from, to) = await RentalWindow(store.contract_id, ct);
        return new StorefrontHoursDto(
            storeId,
            rows.Select(x => new BusinessHourItem(x.DayOfWeek, StorefrontHoursRules.Format(x.OpensAt), StorefrontHoursRules.Format(x.ClosesAt))).ToList(),
            from is { } f && to is { } t ? new RentalWindowDto(StorefrontHoursRules.Format(f), StorefrontHoursRules.Format(t), f > t) : null,
            store.availability_status,
            store.availability_status == "OPEN" && StorefrontHours.IsOpen(rows, StorefrontHours.LocalNow(Now)));
    }

    /// <summary>The hours the ward rents the contract's pitch for (its zone's available_from/to), if it set any.</summary>
    private async Task<(TimeOnly? From, TimeOnly? To)> RentalWindow(long contractId, CancellationToken ct)
    {
        var zone = await db.RentalContracts.AsNoTracking().Where(x => x.contract_id == contractId)
            .Select(x => new { x.slot.zone.available_from, x.slot.zone.available_to }).SingleOrDefaultAsync(ct);
        return zone is { available_from: not null, available_to: not null }
            ? (zone.available_from, zone.available_to)
            : (null, null);
    }

    public async Task<IReadOnlyList<SellerCategoryDto>> Categories(CancellationToken ct)
    {
        await vendors.RequireVendorIdAsync(ct);
        return await db.FoodCategories.AsNoTracking().OrderBy(x => x.category_name)
            .Select(x => new SellerCategoryDto(x.category_id, x.category_name, x.requires_food_safety)).ToListAsync(ct);
    }

    public async Task<SellerMenuDto> Menu(long storeId, CancellationToken ct)
    {
        await OwnedStore(storeId, await vendors.RequireVendorIdAsync(ct), ct);
        var rows = await db.MenuItems.AsNoTracking().Where(x => x.storefront_id == storeId && x.availability_status != "ARCHIVED")
            .OrderBy(x => x.menu_item_id).Select(x => new
            {
                x.menu_item_id, x.storefront_id, x.category_id, x.category.category_name, x.category.requires_food_safety,
                x.item_name, x.description, x.unit_price, x.availability_status, x.image_url,
                Applications = x.FoodSafetyApplicationItems
                    .Select(a => new { a.application.application_status, a.application.expires_on }).ToList(),
            }).ToListAsync(ct);
        var items = rows.Select(x =>
        {
            var expiresOn = x.Applications
                .Where(a => a.application_status == FoodSafetyStatuses.Approved && a.expires_on >= Today)
                .Max(a => a.expires_on);
            var status = expiresOn is not null ? DishFoodSafetyStatuses.Approved
                : !x.requires_food_safety ? DishFoodSafetyStatuses.NotRequired
                : x.Applications.Any(a => FoodSafetyStatuses.Open.Contains(a.application_status)) ? DishFoodSafetyStatuses.Pending
                : DishFoodSafetyStatuses.Missing;
            return new SellerMenuItemDto(x.menu_item_id, x.storefront_id, x.category_id, x.item_name, x.description,
                x.unit_price, x.availability_status, x.image_url, x.category_name, x.requires_food_safety, status, expiresOn);
        }).ToList();
        return new SellerMenuDto(items, MenuRules.MaxActiveItemsPerStorefront);
    }

    public async Task<SellerMenuItemDto> SaveMenu(long storeId, long? itemId, SellerMenuInput input, CancellationToken ct)
    {
        var vendor = await vendors.RequireVendorIdAsync(ct);
        Text(input.Name, 180, "Tên món");
        OptionalText(input.Description, 500);
        if (input.UnitPrice <= 0 || input.UnitPrice > 50_000_000 || decimal.Truncate(input.UnitPrice) != input.UnitPrice)
            throw new DomainRuleException("Giá món phải là số nguyên VND từ 1 đến 50.000.000.");
        if (input.AvailabilityStatus is not ("AVAILABLE" or "SOLD_OUT"))
            throw new DomainRuleException("Trạng thái món không hợp lệ.");
        var imageUrl = string.IsNullOrWhiteSpace(input.ImageUrl) ? null : input.ImageUrl.Trim();
        if (imageUrl is null && !itemId.HasValue) throw new DomainRuleException(MenuImageFiles.Required);
        if (imageUrl is not null)
        {
            var ownerUserId = await db.Vendors.Where(x => x.vendor_id == vendor).Select(x => x.user_id).SingleAsync(ct);
            if (!MenuImageFiles.TryParseUrl(imageUrl, out var owner, out var fileName) || owner != ownerUserId ||
                !await storage.ExistsAsync(MenuImageFiles.StoragePath(owner, fileName), ct))
                throw new DomainRuleException(MenuImageFiles.InvalidUrl);
        }
        long savedId = 0;
        await Write(async () =>
        {
            await OwnedStore(storeId, vendor, ct);
            if (!itemId.HasValue && await db.MenuItems.CountAsync(x => x.storefront_id == storeId &&
                    x.availability_status != "ARCHIVED", ct) >= MenuRules.MaxActiveItemsPerStorefront)
                throw new ConflictException($"Mỗi gian hàng chỉ bán tối đa {MenuRules.MaxActiveItemsPerStorefront} món chủ lực. Gỡ bớt món để thêm món mới.");
            if (!await db.FoodCategories.AnyAsync(x => x.category_id == input.CategoryId, ct))
                throw new NotFoundException("Không tìm thấy danh mục món.");
            var item = itemId.HasValue ? await db.MenuItems.SingleOrDefaultAsync(x =>
                x.menu_item_id == itemId && x.storefront_id == storeId, ct) ?? throw new NotFoundException("Không tìm thấy món.") : new MenuItem();
            if (itemId.HasValue && item.availability_status is "HIDDEN" or "ARCHIVED")
                throw new ConflictException("Món đã bị ẩn hoặc gỡ; không thể tự khôi phục.");
            // A certificate covers a named dish: renaming or recategorising it would carry the
            // approval over to a dish nobody inspected.
            if (itemId.HasValue && (item.item_name != input.Name.Trim() || item.category_id != input.CategoryId) &&
                await db.FoodSafetyApplicationItems.AnyAsync(x => x.menu_item_id == item.menu_item_id &&
                    (FoodSafetyStatuses.Open.Contains(x.application.application_status) ||
                     (x.application.application_status == FoodSafetyStatuses.Approved && x.application.expires_on >= Today)), ct))
                throw new ConflictException("Món đang có hồ sơ hoặc giấy ATTP; không thể đổi tên hay danh mục. Hãy thêm món mới.");
            if (!itemId.HasValue)
            {
                item.storefront_id = storeId;
                item.created_at = Now;
                db.MenuItems.Add(item);
            }
            item.item_name = input.Name.Trim();
            item.description = input.Description?.Trim();
            item.category_id = input.CategoryId;
            item.unit_price = input.UnitPrice;
            item.availability_status = input.AvailabilityStatus;
            if (imageUrl is not null) item.image_url = imageUrl;
            item.updated_at = Now;
            await db.SaveChangesAsync(ct);
            savedId = item.menu_item_id;
            return true;
        }, ct);
        return (await Menu(storeId, ct)).Items.Single(x => x.MenuItemId == savedId);
    }

    public async Task ArchiveMenu(long storeId, long itemId, CancellationToken ct)
    {
        var vendor = await vendors.RequireVendorIdAsync(ct);
        await Write(async () =>
        {
            await OwnedStore(storeId, vendor, ct);
            var item = await db.MenuItems.SingleOrDefaultAsync(x => x.menu_item_id == itemId && x.storefront_id == storeId, ct)
                ?? throw new NotFoundException("Không tìm thấy món.");
            item.availability_status = "ARCHIVED";
            item.updated_at = Now;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    public async Task<IReadOnlyList<CustomerComplaintDto>> Complaints(long orderId, CancellationToken ct)
    {
        await OwnedOrder(orderId, await customers.RequireCustomerUserIdAsync(ct), ct);
        return await db.Complaints.AsNoTracking().Where(x => x.order_id == orderId).OrderByDescending(x => x.complaint_id)
            .Select(x => new CustomerComplaintDto(x.complaint_id, x.order_id, x.complaint_type, x.description,
                x.requested_refund_amount, x.status, x.resolution_notes, DateTime.SpecifyKind(x.created_at, DateTimeKind.Utc))).ToListAsync(ct);
    }

    public async Task<CustomerComplaintDto> Complain(long orderId, CustomerComplaintInput input, CancellationToken ct)
    {
        var customer = await customers.RequireCustomerUserIdAsync(ct);
        Text(input.Description, 1000, "Nội dung khiếu nại");
        if (input.ComplaintType is not ("COMPLAINT" or "REFUND_REQUEST")) throw new DomainRuleException("Loại khiếu nại không hợp lệ.");
        return await Write(async () =>
        {
            var order = await OwnedOrder(orderId, customer, ct);
            if (order.order_status is "PENDING_PAYMENT" or "PLACED")
                throw new DomainRuleException("Đơn chưa được xử lý; bạn có thể hủy đơn từ trang chi tiết.");
            var payment = await db.PaymentTransactions.Where(x => x.order_id == orderId && x.payment_purpose == "ORDER" && x.transaction_status == "SUCCESS")
                .OrderByDescending(x => x.transaction_id).FirstOrDefaultAsync(ct);
            if (payment is null) throw new DomainRuleException("Chỉ gửi khiếu nại cho đơn đã thanh toán.");
            if (await db.Complaints.AnyAsync(x => x.order_id == orderId && (x.status == "OPEN" || x.status == "UNDER_REVIEW"), ct))
                throw new ConflictException("Đơn đã có khiếu nại đang xử lý.");
            var refunded = await db.RefundTransactions.Where(x => x.order_id == orderId && x.refund_status != "FAILED")
                .SumAsync(x => (decimal?)x.amount, ct) ?? 0;
            if (input.ComplaintType == "REFUND_REQUEST" && (input.RequestedRefundAmount is null or <= 0 ||
                input.RequestedRefundAmount > payment.amount - refunded || decimal.Truncate(input.RequestedRefundAmount.Value) != input.RequestedRefundAmount))
                throw new DomainRuleException("Số tiền yêu cầu phải là số nguyên dương và không vượt số tiền còn được hoàn.");
            if (input.ComplaintType == "COMPLAINT" && input.RequestedRefundAmount is not null)
                throw new DomainRuleException("Chỉ yêu cầu hoàn tiền mới được nhập số tiền.");
            var row = new Complaint { order_id = orderId, customer_user_id = customer,
                complaint_type = input.ComplaintType, description = input.Description.Trim(), requested_refund_amount = input.RequestedRefundAmount,
                status = "OPEN", created_at = Now };
            db.Complaints.Add(row);
            await db.SaveChangesAsync(ct);
            return new CustomerComplaintDto(row.complaint_id, orderId, row.complaint_type, row.description,
                row.requested_refund_amount, row.status, null, row.created_at);
        }, ct);
    }

    public async Task<OrderReviewDto?> Review(long orderId, CancellationToken ct)
    {
        await OwnedOrder(orderId, await customers.RequireCustomerUserIdAsync(ct), ct);
        var review = await db.Reviews.AsNoTracking().Where(x => x.order_id == orderId)
            .Select(x => new { x.review_id, x.rating, x.review_text, x.created_at }).SingleOrDefaultAsync(ct);
        return review is null ? null : new OrderReviewDto(review.review_id, review.rating, review.review_text,
            DateTime.SpecifyKind(ReviewRules.EditableUntil(review.created_at), DateTimeKind.Utc));
    }

    public async Task<OrderReviewDto> SaveReview(long orderId, OrderReviewInput input, CancellationToken ct)
    {
        var customer = await customers.RequireCustomerUserIdAsync(ct);
        if (input.Rating is < 1 or > 5) throw new DomainRuleException("Điểm đánh giá từ 1 đến 5.");
        OptionalText(input.Text, 1000);
        return await Write(async () =>
        {
            var order = await OwnedOrder(orderId, customer, ct);
            if (order.order_status != "COMPLETED") throw new DomainRuleException("Chỉ đánh giá đơn đã hoàn tất.");
            var row = await db.Reviews.SingleOrDefaultAsync(x => x.order_id == orderId, ct);
            if (row is not null && await db.ReportedContents.AnyAsync(x => x.content_type == "REVIEW" && x.content_id == row.review_id && x.status == "HIDDEN", ct))
                throw new ForbiddenException("Đánh giá đã bị quản trị viên ẩn.");
            // BR-68: the first submission is open to anyone with a completed order; an edit has to come
            // within the editing period counted from that first submission, not from the last edit.
            if (row is not null && Now > ReviewRules.EditableUntil(row.created_at))
                throw new DomainRuleException(ReviewRules.EditWindowClosed);
            if (row is null)
            {
                row = new Review { order_id = orderId, customer_user_id = customer, created_at = Now };
                db.Reviews.Add(row);
            }
            else row.edited_at = Now;
            row.rating = input.Rating;
            row.review_text = input.Text?.Trim();
            await db.SaveChangesAsync(ct);
            return new OrderReviewDto(row.review_id, row.rating, row.review_text,
                DateTime.SpecifyKind(ReviewRules.EditableUntil(row.created_at), DateTimeKind.Utc));
        }, ct);
    }

    private async Task<Storefront> OwnedStore(long id, long vendor, CancellationToken ct) =>
        await db.Storefronts.SingleOrDefaultAsync(x => x.storefront_id == id && x.registration.vendor_id == vendor, ct)
        ?? throw new NotFoundException("Không tìm thấy gian hàng của bạn.");
    private async Task<Order> OwnedOrder(long id, long customer, CancellationToken ct) =>
        await db.Orders.SingleOrDefaultAsync(x => x.order_id == id && x.customer_user_id == customer, ct)
        ?? throw new NotFoundException("Không tìm thấy đơn hàng của bạn.");
    private static void Text(string? text, int max, string label)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > max)
            throw new DomainRuleException($"{label} bắt buộc và tối đa {max} ký tự.");
    }
    private static void OptionalText(string? text, int max)
    {
        if (text?.Length > max) throw new DomainRuleException($"Mô tả tối đa {max} ký tự.");
    }
    private Task<T> Write<T>(Func<Task<T>> action, CancellationToken ct) => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var result = await action();
        await transaction.CommitAsync(ct);
        return result;
    });
}
