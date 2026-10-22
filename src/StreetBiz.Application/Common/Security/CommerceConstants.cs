namespace StreetBiz.Application.Common.Security;

public static class CartStatuses
{
    public const string Active = "ACTIVE";
    public const string CheckedOut = "CHECKED_OUT";
    public const string Abandoned = "ABANDONED";
}

public static class OrderStatuses
{
    public const string PendingPayment = "PENDING_PAYMENT";
    public const string Placed = "PLACED";
    public const string Accepted = "ACCEPTED";
    public const string Rejected = "REJECTED";
    public const string Preparing = "PREPARING";
    public const string ReadyForPickup = "READY_FOR_PICKUP";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";

    public static bool IsValid(string value) => value is PendingPayment or Placed or Accepted
        or Rejected or Preparing or ReadyForPickup or Completed or Cancelled;

    public static bool IsSellerVisible(string value) =>
        value != PendingPayment && IsValid(value);

    /// <summary>
    /// The order is paid and still waiting to change hands, so a pickup code is
    /// worth showing. An unpaid order has nothing to collect; a finished, rejected
    /// or cancelled one has nothing left to prove.
    /// </summary>
    public static bool IsCollectable(string value) =>
        value is Placed or Accepted or Preparing or ReadyForPickup;
}

/// <summary>
/// REV-02 / BR-68: a review can be edited by its author only for a limited time after it was first
/// submitted. The SRS addendum proposes 7 days and marks the number as awaiting supervisor sign-off,
/// so it lives here, in one place, until it is confirmed.
/// </summary>
public static class ReviewRules
{
    public const int EditWindowDays = 7;
    public static readonly TimeSpan EditWindow = TimeSpan.FromDays(EditWindowDays);

    /// <summary>The last instant a review submitted at <paramref name="submittedAt"/> can still be edited.</summary>
    public static DateTime EditableUntil(DateTime submittedAt) => submittedAt + EditWindow;

    public const string EditWindowClosed =
        "Đã quá thời hạn chỉnh sửa đánh giá (7 ngày kể từ lúc gửi), bạn không thể sửa nữa.";
}

public static class SellerOrderDecisions
{
    public const string Accept = "ACCEPT";
    public const string Reject = "REJECT";
}

public static class PaymentProviders
{
    public const string Momo = "MOMO";
    public const string ZaloPay = "ZALOPAY";

    public static bool IsValid(string value) => value is Momo or ZaloPay;
}

public static class SalesPeriods
{
    public const string Day = "DAY";
    public const string Week = "WEEK";
    public const string Month = "MONTH";

    public static bool IsValid(string value) => value is Day or Week or Month;
}

public static class MarketplaceStorefrontSorts
{
    public const string Distance = "distance";
    public const string Rating = "rating";
    public const string Name = "name";

    public static bool IsValid(string value) => value is Distance or Rating or Name;
}

public static class MarketplaceMenuSorts
{
    public const string Name = "name";
    public const string PriceAsc = "price_asc";
    public const string PriceDesc = "price_desc";

    public static bool IsValid(string value) => value is Name or PriceAsc or PriceDesc;
}

public static class CommerceMessages
{
    // Shown to buyers as-is by the web client, so they are written in Vietnamese.
    public const string StorefrontNotFound = "Không tìm thấy điểm bán.";
    public const string CartEmpty = "Giỏ hàng đang trống.";
    public const string CartItemNotFound = "Món này không còn trong giỏ hàng.";
    public const string MenuItemNotFound = "Không tìm thấy món ăn.";
    public const string MenuItemUnavailable = "Có món trong giỏ đã ngừng bán.";
    public const string StorefrontUnavailable = "Điểm bán hiện không nhận đơn.";
    public const string CartConflict = "Giỏ hàng vừa thay đổi. Vui lòng tải lại và thử lại.";
    public const string CartQuantityLimit = "Mỗi món chỉ đặt tối đa 99 phần.";
    public const string OrderNotFound = "Không tìm thấy đơn hàng.";
    public const string OrderConflict = "Trạng thái đơn hàng vừa thay đổi. Vui lòng tải lại và thử lại.";
    public const string CheckoutAlreadyPending =
        "Bạn có một đơn đang chờ thanh toán. Hãy thanh toán hoặc huỷ đơn đó trước khi sửa giỏ hàng.";
    public const string CheckoutPaymentEnded =
        "Lần thanh toán này đã thất bại hoặc đơn đã bị huỷ. Hãy bấm thanh toán lại để tạo đơn mới.";
    public const string CheckoutAlreadyPaid = "Đơn này đã được thanh toán.";
    public const string PaymentNotFound = "Đơn trả trước chưa có giao dịch thanh toán thành công.";
}
