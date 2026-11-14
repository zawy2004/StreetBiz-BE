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

/// <summary>ADM-02: the audit actions behind an account suspension, also used to read its history back.</summary>
public static class AccountAuditActions
{
    public const string Suspend = "ADM_ACCOUNT_SUSPEND";
    public const string Reactivate = "ADM_ACCOUNT_REACTIVATE";
    public const string EntityType = "UserAccount";
}

public static class PlatformAdministrationMessages
{
    public const string AccountNotFound = "Không tìm thấy tài khoản.";
    public const string AccountConflict = "Trạng thái tài khoản đã thay đổi. Hãy tải lại rồi thử lại.";
    public const string AccountRoleNotManageable =
        "Quản trị nền tảng chỉ quản lý tài khoản Người mua và Hộ kinh doanh.";
    public const string ExportNotFound = "Không tìm thấy báo cáo đã xuất.";
    public const string ExportNotReady = "Báo cáo này chưa sẵn sàng hoặc đã tạo lỗi, không thể tải xuống.";
    public const string ExportFileGone = "Tệp báo cáo không còn trong kho lưu trữ. Hãy xuất lại.";
    public const string ExportTooManyRows = "Báo cáo vượt quá 50.000 dòng. Hãy chọn khoảng thời gian ngắn hơn.";
    public const string ExportFailed = "Không thể tạo tệp báo cáo. Vui lòng thử lại.";
    public const string AccountSelfAction = "Bạn không thể tự khoá chính tài khoản của mình.";

    public const string CategoryNotFound = "The food category was not found.";
    public const string CategoryDuplicate = "A food category with this name already exists.";
    public const string CategoryInUse = "A food category used by menu items cannot be deleted.";
    public const string ReportNotFound = "The reported-content record was not found.";
    public const string ReportConflict = "The report was already reviewed. Refresh and try again.";
    public const string ReportedContentNotFound = "The referenced content no longer exists.";
    public const string RestoreConflict = "The content is no longer hidden. Refresh and try again.";
    public const string ComplaintNotFound = "The order complaint was not found.";
    public const string ComplaintConflict = "The complaint status changed. Refresh and try again.";
    public const string RefundNotAllowed = "A refund is only allowed for a refund request.";
    public const string PaymentNotFound = "No successful order payment is available for a refund.";
    public const string RefundExceedsLimit = "The refund exceeds the requested or remaining paid amount.";
}

/// <summary>ADM-07 report kinds, kept in ReportExports.report_type (max 50 characters).</summary>
public static class PlatformReportTypes
{
    public const string OperationsSummary = "OPERATIONS_SUMMARY";
    public const string Orders = "ORDERS";
    public const string Moderation = "MODERATION";
    public const string Accounts = "ACCOUNTS";

    public static bool IsValid(string value) => value is OperationsSummary or Orders or Moderation or Accounts;
}

public static class PlatformReportFormats
{
    public const string Xlsx = "xlsx";
    public const string Csv = "csv";

    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string CsvContentType = "text/csv; charset=utf-8";

    public static bool IsValid(string value) => value is Xlsx or Csv;
    public static string ContentType(string format) => format == Csv ? CsvContentType : XlsxContentType;
}

/// <summary>ReportExports.export_status. The schema allows PENDING, READY and FAILED.</summary>
public static class ExportStatuses
{
    public const string Pending = "PENDING";
    public const string Ready = "READY";
    public const string Failed = "FAILED";
}

public static class PlatformReportLimits
{
    public const int MaxDays = 366;
    public const int MaxRows = 50_000;
    public const string AuditAction = "ADM_REPORT_EXPORT";
    public const string AuditEntityType = "ReportExport";
}
