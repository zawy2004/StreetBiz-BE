using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Security;

public static class PermitScanContexts
{
    public const string PublicCheck = "PUBLIC_CHECK";
}

public static class PermitScanResults
{
    public const string NotFound = "NOT_FOUND";
}

public static class VendorReportStatuses
{
    public const string Pending = "PENDING";
    public const string Reviewed = "REVIEWED";
    public const string Dismissed = "DISMISSED";
}

/// <summary>VendorReports.report_type (CK_VendorReports_Type).</summary>
public static class VendorReportTypes
{
    public const string General = "GENERAL";
    /// <summary>"Bị tính cao hơn giá niêm yết" (BUY-07): carries server-computed price evidence.</summary>
    public const string Overcharge = "OVERCHARGE";
    /// <summary>"Quầy không treo bảng giá".</summary>
    public const string PriceNotPosted = "PRICE_NOT_POSTED";

    public static readonly IReadOnlyList<string> All = [General, Overcharge, PriceNotPosted];

    public static bool IsValid(string? value) => value is not null && All.Contains(value);

    /// <summary>The violation type an officer is offered when raising a biên bản from such a report.</summary>
    public static string? SuggestedViolationType(string reportType) => reportType switch
    {
        Overcharge => "PRICE_ABOVE_POSTED",
        PriceNotPosted => "PRICE_NOT_POSTED",
        _ => null,
    };
}

/// <summary>VendorReports.resolution (CK_VendorReports_Resolution): how an officer closed a report.</summary>
public static class VendorReportResolutions
{
    public const string Dismissed = "DISMISSED";
    public const string Mediated = "MEDIATED";
    /// <summary>Set only by recording a violation from the report, never through the resolve endpoint.</summary>
    public const string ViolationRecorded = "VIOLATION_RECORDED";

    /// <summary>What POST /api/ward/vendor-reports/{id}/resolve accepts.</summary>
    public static bool IsResolveOutcome(string? value) => value is Dismissed or Mediated;

    public static string StatusFor(string resolution) =>
        resolution == Dismissed ? VendorReportStatuses.Dismissed : VendorReportStatuses.Reviewed;
}

public static class VendorReportNotificationTypes
{
    /// <summary>To the officers of the report's ward.</summary>
    public const string New = "VENDOR_REPORT_NEW";
    /// <summary>To the reported vendor, without the reporter's identity (BR privacy, Luật 91/2025).</summary>
    public const string AgainstYou = "VENDOR_REPORT_AGAINST_YOU";
    /// <summary>To the reporter and to the vendor when an officer closes the report.</summary>
    public const string Resolved = "VENDOR_REPORT_RESOLVED";

    public const string EntityType = "VendorReport";
}

/// <summary>BUY-07 / WARD-19 limits that are not configuration.</summary>
public static class VendorReportRules
{
    public const int NoteMaxLength = 500;
    /// <summary>A complaint about a purchase is taken within this many days of it.</summary>
    public const int PurchaseWindowDays = 7;
    /// <summary>A phone's clock may run a little ahead; a "now" within this is taken as now.</summary>
    public static readonly TimeSpan PurchaseClockSkew = TimeSpan.FromMinutes(5);
    public const int MaxQuantity = 1_000;
    public const decimal MaxChargedAmount = 1_000_000_000m;
    /// <summary>The ward's detail view counts this vendor's reports over this many days (context only).</summary>
    public const int VendorHistoryDays = 90;

    /// <summary>
    /// The purchase time a complaint is judged at, in UTC: not before the window, not in the future.
    /// A time within <see cref="PurchaseClockSkew"/> ahead of the server is taken as now.
    /// </summary>
    public static DateTime PurchaseTime(DateTimeOffset purchasedAt, DateTime nowUtc)
    {
        var utc = purchasedAt.UtcDateTime;
        if (utc > nowUtc + PurchaseClockSkew)
        {
            throw new DomainRuleException(CommunityMessages.PurchaseInFuture);
        }

        if (utc < nowUtc.AddDays(-PurchaseWindowDays))
        {
            throw new DomainRuleException(CommunityMessages.PurchaseTooOld);
        }

        return utc > nowUtc ? nowUtc : utc;
    }

    /// <summary>
    /// The end (UTC+7) of the day <paramref name="workingDays"/> working days after
    /// <paramref name="today"/>: weekends and public holidays are skipped (BusinessCalendar). An
    /// internal handling target set by VendorReports:DueWorkingDays, not a statutory deadline.
    /// </summary>
    public static DateTime DueAt(DateOnly today, int workingDays) =>
        BusinessCalendar.EndOfDayUtc(BusinessCalendar.AddWorkingDays(today, workingDays));

    public static bool IsOverdue(string status, DateTime? dueAt, DateTime nowUtc) =>
        status == VendorReportStatuses.Pending && dueAt is { } due && due < nowUtc;
}

/// <summary>Bound from the "VendorReports" configuration section.</summary>
public sealed class VendorReportSettings
{
    public const string SectionName = "VendorReports";

    /// <summary>Working days the ward gives itself to close a report (D4: default 7).</summary>
    public int DueWorkingDays { get; set; } = 7;
}

public static class CommunityMessages
{
    public const string VendorNotFound = "Active vendor not found.";
    public const string ReportReferenceMismatch = "The selected slot or permit does not belong to this vendor.";
    public const string DuplicatePendingReport =
        "Bạn đã có một phản ánh về người bán này đang chờ phường xử lý. Hãy chờ kết quả trước khi gửi phản ánh mới.";
    public const string MenuItemNotFound = "Không tìm thấy món này trong thực đơn của người bán.";
    public const string NotListedAtPurchase = "Món này không có trên bảng giá của quầy vào thời điểm bạn mua.";
    public const string NotAbovePostedPrice =
        "Số tiền bị tính không cao hơn giá niêm yết lúc mua. Nếu có vấn đề khác, hãy chọn \"Phản ánh khác\".";
    public const string PurchaseInFuture = "Giờ mua không được ở tương lai.";
    public const string PurchaseTooOld = "Chỉ nhận khiếu nại giá trong 7 ngày kể từ lúc mua.";
    public const string ReportNotFound = "Không tìm thấy phản ánh trong phường của bạn.";
    public const string ReportAlreadyClosed = "Phản ánh đã được xử lý. Tải lại để xem kết quả mới nhất.";
}
