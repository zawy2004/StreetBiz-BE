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
    public const string StorefrontNotFound = "The storefront was not found.";
    public const string CartEmpty = "The active cart is empty.";
    public const string CartItemNotFound = "The cart item was not found.";
    public const string MenuItemNotFound = "The menu item was not found.";
    public const string MenuItemUnavailable = "A menu item is no longer available.";
    public const string StorefrontUnavailable = "The storefront is not open for orders.";
    public const string CartConflict = "The cart changed. Refresh and try again.";
    public const string OrderNotFound = "The order was not found.";
    public const string OrderConflict = "The order status changed. Refresh and try again.";
    public const string PaymentNotFound = "The prepaid order has no successful payment.";
}
