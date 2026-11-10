using System.Globalization;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.CommunityVendors;

/// <summary>
/// The wording of every notice a vendor report sends (BUY-05/07, WARD-19). Personal data rule
/// (Luật 91/2025/QH15): what reaches the vendor names neither the reporter nor carries any text the
/// reporter or the officer typed, since free text may name the reporter. The vendor learns what was
/// reported (type, dish, amounts) and how it ended, enough to refund the customer of their own accord.
/// </summary>
public static class VendorReportNotices
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public static string Money(decimal amount) => amount.ToString("N0", Vi) + "đ";

    public static string TypeLabel(string reportType) => reportType switch
    {
        VendorReportTypes.Overcharge => "Bán cao hơn giá niêm yết",
        VendorReportTypes.PriceNotPosted => "Không treo bảng giá",
        _ => "Phản ánh khác",
    };

    /// <summary>"quầy "X"", or the slot when the vendor is unknown.</summary>
    public static string Subject(string? vendorName, string? slotCode) =>
        !string.IsNullOrWhiteSpace(vendorName) ? $"quầy \"{vendorName}\""
        : !string.IsNullOrWhiteSpace(slotCode) ? $"quầy tại ô {slotCode}"
        : "quầy được phản ánh";

    /// <summary>The Vietnamese day a due instant (the end of that day, UTC+7) closes.</summary>
    public static string DueDay(DateTime dueAtUtc) =>
        BusinessCalendar.DateOf(dueAtUtc.AddTicks(-1)).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    public static NewVendorReportNotices ForNewReport(
        string reportType,
        string vendorName,
        string? slotCode,
        DateTime dueAtUtc,
        PostedPriceRow? price,
        int? quantity,
        decimal? chargedAmount,
        decimal? postedAmount)
    {
        var ward = new NotificationText(
            "Có phản ánh mới cần xử lý",
            $"{TypeLabel(reportType)}: quầy \"{vendorName}\"{(slotCode is null ? "" : $" (ô {slotCode})")}. "
            + $"Hạn xử lý: hết ngày {DueDay(dueAtUtc)}.");

        var vendor = reportType switch
        {
            VendorReportTypes.Overcharge when price is not null && quantity is { } qty
                && chargedAmount is { } charged && postedAmount is { } posted => new NotificationText(
                "Có khiếu nại về giá tại quầy của bạn",
                $"Một khách báo bị tính {Money(charged)} cho {qty} {price.PriceUnit} {price.ItemName}, "
                + $"trong khi giá niêm yết lúc mua là {Money(posted)}. Hãy kiểm tra và chủ động hoàn phần "
                + "chênh lệch nếu tính sai; phường sẽ xem xét và báo kết quả."),
            VendorReportTypes.PriceNotPosted => new NotificationText(
                "Có phản ánh về bảng giá tại quầy của bạn",
                $"Một khách phản ánh quầy \"{vendorName}\" không treo bảng giá. Luật Giá yêu cầu niêm yết giá "
                + "tại quầy: hãy in bảng giá từ mục Thực đơn và treo tại quầy."),
            _ => new NotificationText(
                "Có phản ánh về quầy của bạn",
                $"Phường đã nhận một phản ánh về quầy \"{vendorName}\" và sẽ xem xét."),
        };

        return new NewVendorReportNotices(ward, vendor);
    }

    /// <summary>To the reporter: the outcome with the officer's note, which is written for them.</summary>
    public static NotificationText ResolvedForReporter(string resolution, string subject, string? note, decimal? refundAmount)
    {
        var text = resolution switch
        {
            VendorReportResolutions.Dismissed => $"Phường không chấp nhận phản ánh về {subject}. Lý do: {note}",
            VendorReportResolutions.Mediated => $"Phường đã hòa giải phản ánh về {subject}{Refund(refundAmount)}. {note}",
            _ => $"Phường đã lập biên bản vi phạm đối với {subject} từ phản ánh của bạn. {note}",
        };
        return new NotificationText("Phản ánh của bạn đã có kết quả", text.TrimEnd());
    }

    /// <summary>To the vendor: the outcome only, never the note (it may name the reporter).</summary>
    public static NotificationText ResolvedForVendor(string resolution, string subject, decimal? refundAmount) =>
        new("Phản ánh về quầy của bạn đã được đóng", resolution switch
        {
            VendorReportResolutions.Dismissed => $"Phường đã xem xét và không chấp nhận phản ánh về {subject}.",
            VendorReportResolutions.Mediated => $"Phường ghi nhận đã hòa giải phản ánh về {subject}{Refund(refundAmount)}.",
            _ => $"Phường đã lập biên bản vi phạm từ phản ánh về {subject}.",
        });

    private static string Refund(decimal? amount) =>
        amount is > 0 ? $", người bán hoàn lại {Money(amount.Value)}" : string.Empty;
}
