namespace StreetBiz.Application.Common.Security;

public static class ReportedContentStatuses
{
    public const string Pending = "PENDING";
    public const string Dismissed = "DISMISSED";
    public const string Hidden = "HIDDEN";

    public static bool IsValid(string value) => value is Pending or Dismissed or Hidden;
}

public static class ReportedContentTypes
{
    public const string Storefront = "STOREFRONT";
    public const string MenuItem = "MENU_ITEM";
    public const string Review = "REVIEW";
}

public static class ContentModerationDecisions
{
    public const string Dismiss = "DISMISS";
    public const string Hide = "HIDE";

    public static bool IsValid(string value) => value is Dismiss or Hide;
}

public static class ComplaintStatuses
{
    public const string Open = "OPEN";
    public const string UnderReview = "UNDER_REVIEW";
    public const string Resolved = "RESOLVED";
    public const string Rejected = "REJECTED";

    public static bool IsValid(string value) => value is Open or UnderReview or Resolved or Rejected;
}

public static class ComplaintTypes
{
    public const string Complaint = "COMPLAINT";
    public const string RefundRequest = "REFUND_REQUEST";
}

public static class ComplaintDecisions
{
    public const string Resolve = "RESOLVE";
    public const string Reject = "REJECT";

    public static bool IsValid(string value) => value is Resolve or Reject;
}

public static class PlatformAdministrationMessages
{
    public const string CategoryNotFound = "The food category was not found.";
    public const string CategoryDuplicate = "A food category with this name already exists.";
    public const string CategoryInUse = "A food category used by menu items cannot be deleted.";
    public const string ReportNotFound = "The reported-content record was not found.";
    public const string ReportConflict = "The report was already reviewed. Refresh and try again.";
    public const string ReportedContentNotFound = "The referenced content no longer exists.";
    public const string ComplaintNotFound = "The order complaint was not found.";
    public const string ComplaintConflict = "The complaint status changed. Refresh and try again.";
    public const string RefundNotAllowed = "A refund is only allowed for a refund request.";
    public const string PaymentNotFound = "No successful order payment is available for a refund.";
    public const string RefundExceedsLimit = "The refund exceeds the requested or remaining paid amount.";
}
