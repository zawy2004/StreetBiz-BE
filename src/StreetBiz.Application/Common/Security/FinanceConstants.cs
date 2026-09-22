namespace StreetBiz.Application.Common.Security;

/// <summary>PaymentTransactions.payment_purpose values (DB CHECK CK_PaymentTransactions_Purpose).</summary>
public static class PaymentPurposes
{
    public const string RentalFee = "RENTAL_FEE";
    public const string Penalty = "PENALTY";
    public const string Order = "ORDER";

    public static bool IsValid(string value) => value is RentalFee or Penalty or Order;

    /// <summary>True for the two purposes owned by the finance module (fee and penalty).</summary>
    public static bool IsFinance(string? value) => value is RentalFee or Penalty;
}

/// <summary>
/// FeeScheduleItems.item_status values (DB CHECK CK_FeeScheduleItems_Status).
/// <see cref="DebtStatuses"/> holds the subset the SIDE module already needed.
/// </summary>
public static class FeeItemStatuses
{
    public const string Pending = "PENDING";
    public const string Paid = "PAID";
    public const string Overdue = "OVERDUE";

    /// <summary>A fee instalment still owed: PENDING or OVERDUE.</summary>
    public static bool IsOutstanding(string value) => value is Pending or Overdue;
}

/// <summary>Penalties.penalty_status values (DB CHECK CK_Penalties_Status).</summary>
public static class PenaltyStatuses
{
    public const string Unpaid = "UNPAID";
    public const string Paid = "PAID";
    public const string Waived = "WAIVED";
    public const string Cancelled = "CANCELLED";

    /// <summary>Only an UNPAID penalty can be paid; WAIVED and CANCELLED are closed.</summary>
    public static bool IsPayable(string value) => value == Unpaid;
}

/// <summary>PaymentTransactions.transaction_status values (DB CHECK CK_PaymentTransactions_Status).</summary>
public static class PaymentStatuses
{
    public const string Pending = "PENDING";
    public const string Success = "SUCCESS";
    public const string Failed = "FAILED";

    /// <summary>SUCCESS and FAILED are terminal: a repeated callback must not re-apply them.</summary>
    public static bool IsTerminal(string value) => value is Success or Failed;
}

/// <summary>PaymentCallbackEvents.processing_result values (DB CHECK CK_PaymentCallbackEvents_Result).</summary>
public static class CallbackResults
{
    public const string Applied = "APPLIED";
    public const string Duplicate = "DUPLICATE";
    public const string Unmatched = "UNMATCHED";
    public const string Rejected = "REJECTED";
}

/// <summary>Violations.source values (DB CHECK CK_Violations_Source).</summary>
public static class ViolationSources
{
    public const string OnSite = "ON_SITE";
    public const string CustomerReport = "CUSTOMER_REPORT";
}

/// <summary>ReportExports.export_status values (DB CHECK CK_ReportExports_Status).</summary>
public static class ReportExportStatuses
{
    public const string Pending = "PENDING";
    public const string Ready = "READY";
    public const string Failed = "FAILED";
}

/// <summary>Notifications.notification_type values raised by the finance module (free text column).</summary>
public static class FinanceNotificationTypes
{
    public const string Fee = "FEE";
    public const string Penalty = "PENALTY";
    public const string Invoice = "INVOICE";
}

/// <summary>Messages for the Fee, Payment, Invoice and Reporting workflow (SRS MSG27-MSG32).</summary>
public static class FinanceMessages
{
    public const string ContractNotFound = "Không tìm thấy hợp đồng thuê.";
    public const string ContractNotActive = "Hợp đồng không ở trạng thái còn hiệu lực.";
    public const string ZoneMissingPrice = "Khu vực của ô chưa được cấu hình giá thuê.";
    public const string FeeScheduleNotFound = "Không tìm thấy lịch phí của hợp đồng.";

    /// <summary>
    /// Regenerating a schedule the vendor has already paid into would strand those payments on a
    /// superseded revision; reworking a part-paid contract needs a proration decision BR-18 does
    /// not make.
    /// </summary>
    public const string ScheduleAlreadyPaidInto =
        "Hợp đồng đã có kỳ phí được thanh toán nên không thể sinh lại lịch phí.";
    public const string FeeItemNotFound = "Không tìm thấy kỳ phí.";
    public const string FeeItemNotPayable = "Kỳ phí này đã được thanh toán.";
    public const string PenaltyNotFound = "Không tìm thấy biên bản phạt.";
    public const string PenaltyNotPayable = "Biên bản phạt này không ở trạng thái chờ thanh toán.";
    public const string InvoiceNotFound = "Không tìm thấy hoá đơn.";
    public const string TransactionNotFound = "Không tìm thấy giao dịch thanh toán.";

    /// <summary>MSG27.</summary>
    public const string FeePaid = "Đã thanh toán phí thuê ô. Hoá đơn đã được phát hành.";

    /// <summary>MSG29.</summary>
    public const string FeePaymentFailed = "Thanh toán phí thuê ô không thành công. Vui lòng thử lại.";

    /// <summary>MSG32.</summary>
    public const string PenaltyPaid = "Đã thanh toán tiền phạt.";
}
