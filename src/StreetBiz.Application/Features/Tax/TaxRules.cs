using System.Globalization;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Features.Tax;

// The rules of the household tax module as pure functions: nothing here reads a database or a
// clock (callers pass "today"). Every threshold and rate comes from the TaxPolicyRow in force,
// never from a constant, because the law changed twice in 2026 alone.

public static class TaxMessages
{
    public const string NoPolicy =
        "Chưa có chính sách thuế nào được công bố. Quản trị viên cần công bố chính sách cho năm này.";

    public const string Disclaimer =
        "Số liệu để tham khảo và tự đối chiếu. StreetBiz không khai, không nộp và không thu thuế thay hộ " +
        "kinh doanh; mọi khai báo thực hiện trên cổng của cơ quan thuế.";

    public const string EntryDateRange =
        "Ngày bán phải từ 01/01/2026 đến hôm nay và không quá 2 năm về trước.";

    public const string YearNotStarted = "Chưa đến năm này nên chưa có số liệu.";
    public const string PeriodNotRequired = "Kỳ này không thuộc diện phải khai vì doanh thu chưa vượt ngưỡng.";
    public const string EntryNotFound = "Không tìm thấy khoản doanh thu này.";
    public const string EntryAlreadyVoided = "Khoản doanh thu này đã được huỷ trước đó.";
    public const string UnknownIndustry = "Ngành nghề không có trong chính sách thuế hiện hành.";
    public const string DeclarationNotFound = "Không tìm thấy kỳ khai này.";
    public const string PeriodNotEnded = "Kỳ này chưa kết thúc nên chưa thể xác nhận đã nộp.";
    public const string FiledOnRange = "Ngày nộp phải sau khi kỳ kết thúc và không ở tương lai.";
    public const string ReceiptNotYours = "Tệp biên nhận không hợp lệ hoặc không phải của bạn.";
    public const string ReferralNotPending = "Mục này đã được xử lý hoặc không thuộc phường của bạn.";
    public const string NoRecipients = "Không có hộ nào thuộc nhóm nhận thông báo này.";
    public const string PolicyYearRange = "Năm áp dụng phải từ 2026 trở đi.";
    public const string PolicyNeedsRates =
        "Bảng tỷ lệ phải có đủ hai ngành FOOD_SERVICE và OTHER, mã ngành không được trùng.";
}

public static class TaxFormat
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    /// <summary>"1 tỷ đồng", "500 triệu đồng", "2,5 tỷ đồng", "850.000 đồng".</summary>
    public static string Money(decimal value)
    {
        if (value >= 1_000_000_000m)
        {
            return $"{(value / 1_000_000_000m).ToString("0.##", Vi)} tỷ đồng";
        }

        if (value >= 1_000_000m && value % 1_000_000m == 0)
        {
            return $"{(value / 1_000_000m).ToString("0", Vi)} triệu đồng";
        }

        return $"{value.ToString("N0", Vi)} đồng";
    }

    public static string Day(DateOnly date) => date.ToString("dd/MM/yyyy", Vi);
}

/// <summary>The revenue groups above and below the no-tax threshold (Nghị định 68/2026, 141/2026).</summary>
public static class TaxGroups
{
    public const string One = "G1";
    public const string Two = "G2";
    public const string Three = "G3";
    public const string Four = "G4";

    public static string Of(decimal revenue, TaxPolicyRow policy) =>
        revenue <= policy.RevenueThreshold ? One
        : revenue <= policy.Group2Ceiling ? Two
        : revenue <= policy.Group3Ceiling ? Three
        : Four;

    public static string Describe(string group, TaxPolicyRow policy) => group switch
    {
        One => $"Doanh thu đến {TaxFormat.Money(policy.RevenueThreshold)}/năm",
        Two => $"Doanh thu trên {TaxFormat.Money(policy.RevenueThreshold)} đến {TaxFormat.Money(policy.Group2Ceiling)}/năm",
        Three => $"Doanh thu trên {TaxFormat.Money(policy.Group2Ceiling)} đến {TaxFormat.Money(policy.Group3Ceiling)}/năm",
        _ => $"Doanh thu trên {TaxFormat.Money(policy.Group3Ceiling)}/năm",
    };

    /// <summary>The PIT rate on profit for a group above the threshold; 0 for group 1.</summary>
    public static decimal PitProfitRate(string group, TaxPolicyRow policy) => group switch
    {
        Two => policy.PitProfitRateG2,
        Three => policy.PitProfitRateG3,
        Four => policy.PitProfitRateG4,
        _ => 0m,
    };
}

/// <summary>How close a household's revenue is to the no-tax threshold.</summary>
public static class ThresholdLevels
{
    public const string Safe = "SAFE";
    public const string Near = "NEAR";
    public const string Over = "OVER";

    public static string Of(decimal revenue, TaxPolicyRow policy) =>
        revenue > policy.RevenueThreshold ? Over
        : revenue >= decimal.Round(policy.RevenueThreshold * policy.WarnRatio) ? Near
        : Safe;
}

public static class RevenueChannels
{
    public const string Cash = "CASH";
    public const string BankTransfer = "BANK_TRANSFER";
    public const string Ewallet = "EWALLET";
    public const string Other = "OTHER";

    public static bool IsValid(string? value) => value is Cash or BankTransfer or Ewallet or Other;
}

public static class TaxPeriodTypes
{
    public const string Year = "YEAR";
    public const string Quarter = "QUARTER";
}

public static class TaxPeriodStatuses
{
    public const string Filed = "FILED";
    public const string NotRequired = "NOT_REQUIRED";
    public const string NotYet = "NOT_YET";
    public const string Open = "OPEN";
    public const string DueSoon = "DUE_SOON";
    public const string Overdue = "OVERDUE";
}

// ---------------------------------------------------------------------------------------------

public sealed record RevenueProjection(
    bool Enough,
    int DaysOfData,
    decimal DailyRate,
    decimal ProjectedRevenue,
    bool WillCross,
    DateOnly? CrossingDate);

/// <summary>
/// A plain, explainable forecast: the average daily revenue since the household first sold this
/// year (days before today, so a day still being recorded does not drag it down) carried to
/// 31 December. A household needs <see cref="MinDays"/> days of sales before it is forecast.
/// </summary>
public static class RevenueProjections
{
    public const int MinDays = 14;

    public static RevenueProjection Project(
        decimal revenueToDate, DateOnly? firstRevenue, DateOnly today, int taxYear, decimal threshold)
    {
        var yearStart = new DateOnly(taxYear, 1, 1);
        var yearEnd = new DateOnly(taxYear, 12, 31);

        if (today > yearEnd)
        {
            var days = yearEnd.DayNumber - yearStart.DayNumber + 1;
            return new RevenueProjection(true, days, Round(revenueToDate / days), revenueToDate,
                revenueToDate > threshold, null);
        }

        if (today < yearStart || firstRevenue is null || revenueToDate <= 0)
        {
            return new RevenueProjection(false, 0, 0, revenueToDate, revenueToDate > threshold, null);
        }

        var start = firstRevenue.Value > yearStart ? firstRevenue.Value : yearStart;
        var daysOfData = today.DayNumber - start.DayNumber;
        if (daysOfData < MinDays)
        {
            return new RevenueProjection(false, Math.Max(daysOfData, 0), 0, revenueToDate, revenueToDate > threshold, null);
        }

        var rate = revenueToDate / daysOfData;
        var remaining = yearEnd.DayNumber - today.DayNumber;
        var projected = Round(revenueToDate + rate * remaining);

        if (revenueToDate > threshold)
        {
            return new RevenueProjection(true, daysOfData, Round(rate), projected, true, null);
        }

        DateOnly? crossing = null;
        if (rate > 0)
        {
            var needed = (int)Math.Ceiling((threshold - revenueToDate) / rate);
            var date = today.AddDays(Math.Max(needed, 1));
            if (date <= yearEnd)
            {
                crossing = date;
            }
        }

        return new RevenueProjection(true, daysOfData, Round(rate), projected, crossing is not null, crossing);
    }

    private static decimal Round(decimal value) => decimal.Round(value, 0, MidpointRounding.AwayFromZero);
}

// ---------------------------------------------------------------------------------------------

public sealed record TaxEstimate(
    decimal Revenue,
    string Group,
    bool Taxable,
    decimal VatRate,
    decimal PitRate,
    decimal Vat,
    decimal PitBase,
    decimal PitOnRevenue,
    decimal TotalOnRevenue,
    decimal EffectiveRate,
    decimal? CostRatio,
    decimal? Profit,
    decimal? PitProfitRate,
    decimal? PitOnProfit,
    decimal? TotalOnProfit,
    string? Cheaper);

/// <summary>
/// What a household above the threshold would owe, by the revenue method (VAT and PIT as a share of
/// revenue) and, when it gives its costs, by the profit method (PIT on revenue minus costs). An
/// estimate for planning, never a figure to file.
/// </summary>
public static class TaxEstimator
{
    public static TaxEstimate Estimate(
        decimal revenue, TaxPolicyRow policy, TaxIndustryRateRow industry, decimal? costRatio)
    {
        revenue = Math.Max(revenue, 0);
        var group = TaxGroups.Of(revenue, policy);
        var taxable = revenue > policy.RevenueThreshold;

        var vat = taxable ? Round(revenue * industry.VatRate) : 0m;
        var pitBase = !taxable ? 0m : policy.PitDeductsThreshold ? revenue - policy.RevenueThreshold : revenue;
        var pitOnRevenue = Round(pitBase * industry.PitRate);
        var totalOnRevenue = vat + pitOnRevenue;
        var effective = revenue > 0 ? Math.Round(totalOnRevenue / revenue, 4) : 0m;

        decimal? profit = null;
        decimal? profitRate = null;
        decimal? pitOnProfit = null;
        decimal? totalOnProfit = null;
        string? cheaper = null;
        if (taxable && costRatio is { } ratio)
        {
            ratio = Math.Clamp(ratio, 0m, 0.99m);
            profit = Round(revenue * (1 - ratio));
            profitRate = TaxGroups.PitProfitRate(group, policy);
            pitOnProfit = Round(profit.Value * profitRate.Value);
            totalOnProfit = vat + pitOnProfit.Value;
            cheaper = totalOnProfit < totalOnRevenue ? "PROFIT" : "REVENUE";
        }

        return new TaxEstimate(
            revenue, group, taxable, industry.VatRate, industry.PitRate, vat, pitBase, pitOnRevenue,
            totalOnRevenue, effective, costRatio is null ? null : Math.Clamp(costRatio.Value, 0m, 0.99m),
            profit, profitRate, pitOnProfit, totalOnProfit, cheaper);
    }

    private static decimal Round(decimal value) => decimal.Round(value, 0, MidpointRounding.AwayFromZero);
}

// ---------------------------------------------------------------------------------------------

public sealed record TaxPeriod(string Type, int No, string Label, DateOnly Start, DateOnly End, DateOnly Due);

/// <summary>
/// The calendar of a tax year: the yearly revenue notice (due on the policy's day of the next year)
/// and the four quarters (declared by the end of the month after the quarter, for a household above
/// the threshold). Every date is a Vietnamese calendar day.
/// </summary>
public static class TaxCalendar
{
    /// <summary>A period still open this many days before its due date counts as "due soon".</summary>
    public const int DueSoonDays = 14;

    public static IReadOnlyList<TaxPeriod> Periods(int taxYear, TaxPolicyRow policy)
    {
        var periods = new List<TaxPeriod>
        {
            YearNotice(taxYear, policy),
        };
        for (var quarter = 1; quarter <= 4; quarter++)
        {
            periods.Add(Quarter(taxYear, quarter));
        }

        return periods;
    }

    public static TaxPeriod? Find(int taxYear, string type, int no, TaxPolicyRow policy) =>
        type switch
        {
            TaxPeriodTypes.Year when no == 0 => YearNotice(taxYear, policy),
            TaxPeriodTypes.Quarter when no is >= 1 and <= 4 => Quarter(taxYear, no),
            _ => null,
        };

    public static TaxPeriod YearNotice(int taxYear, TaxPolicyRow policy)
    {
        var dueYear = taxYear + 1;
        var day = Math.Min(policy.AnnualNoticeDay, DateTime.DaysInMonth(dueYear, policy.AnnualNoticeMonth));
        return new TaxPeriod(
            TaxPeriodTypes.Year, 0, $"Thông báo doanh thu năm {taxYear}",
            new DateOnly(taxYear, 1, 1), new DateOnly(taxYear, 12, 31),
            new DateOnly(dueYear, policy.AnnualNoticeMonth, day));
    }

    public static TaxPeriod Quarter(int taxYear, int quarter)
    {
        var startMonth = (quarter - 1) * 3 + 1;
        var endMonth = startMonth + 2;
        var end = new DateOnly(taxYear, endMonth, DateTime.DaysInMonth(taxYear, endMonth));
        var dueMonthDate = end.AddMonths(1);
        var due = new DateOnly(dueMonthDate.Year, dueMonthDate.Month, DateTime.DaysInMonth(dueMonthDate.Year, dueMonthDate.Month));
        return new TaxPeriod(
            TaxPeriodTypes.Quarter, quarter, $"Khai thuế quý {quarter}/{taxYear}",
            new DateOnly(taxYear, startMonth, 1), end, due);
    }

    public static bool CanFile(TaxPeriod period, DateOnly today) => today > period.End;

    public static string Status(TaxPeriod period, DateOnly today, bool filed, bool required)
    {
        if (filed)
        {
            return TaxPeriodStatuses.Filed;
        }

        if (!required)
        {
            return TaxPeriodStatuses.NotRequired;
        }

        if (today <= period.End)
        {
            return TaxPeriodStatuses.NotYet;
        }

        if (today > period.Due)
        {
            return TaxPeriodStatuses.Overdue;
        }

        return period.Due.DayNumber - today.DayNumber <= DueSoonDays
            ? TaxPeriodStatuses.DueSoon
            : TaxPeriodStatuses.Open;
    }
}

// ---------------------------------------------------------------------------------------------

public sealed record TaxObligation(string Code, string Title, string Detail, string Kind, bool? Done);

/// <summary>
/// What a household has to do at its revenue group, in plain words. Kept in code, not data, because
/// it is guidance wording rather than a parameter; the numbers inside it come from the policy.
/// </summary>
public static class TaxObligations
{
    public const string Todo = "TODO";
    public const string Info = "INFO";

    public static IReadOnlyList<TaxObligation> For(
        string group, TaxPolicyRow policy, bool hasTaxCode, bool eInvoiceRegistered)
    {
        var list = new List<TaxObligation>();
        if (group == TaxGroups.One)
        {
            list.Add(new TaxObligation(
                "NO_VAT_PIT",
                "Chưa phải nộp thuế GTGT và TNCN",
                $"Doanh thu đến {TaxFormat.Money(policy.RevenueThreshold)}/năm: không chịu thuế giá trị gia tăng, " +
                "không nộp thuế thu nhập cá nhân.",
                Info, null));
        }

        list.Add(TaxCode(hasTaxCode));
        list.Add(new TaxObligation(
            "KEEP_REVENUE_BOOK",
            "Ghi chép doanh thu hằng ngày",
            "StreetBiz tự ghi doanh thu từ đơn hàng đã hoàn tất. Bạn nhập thêm phần bán trực tiếp ở Sổ doanh thu.",
            Todo, null));
        list.Add(new TaxObligation(
            "ANNUAL_REVENUE_NOTICE",
            "Thông báo doanh thu thực tế của năm cho cơ quan thuế",
            $"Thực hiện một lần mỗi năm, hạn ngày {policy.AnnualNoticeDay:00}/{policy.AnnualNoticeMonth:00} của năm sau.",
            Todo, null));

        if (group != TaxGroups.One)
        {
            list.AddRange(OnceOver(group, policy, eInvoiceRegistered));
        }

        return list;
    }

    /// <summary>The extra duties that start when revenue goes above the threshold.</summary>
    public static IReadOnlyList<TaxObligation> OnceOver(string group, TaxPolicyRow policy, bool eInvoiceRegistered)
    {
        var list = new List<TaxObligation>
        {
            new(
                "DECLARE_PERIODIC",
                group == TaxGroups.Four ? "Khai thuế theo tháng như doanh nghiệp" : "Khai thuế GTGT và TNCN theo quý",
                group == TaxGroups.Four
                    ? "Doanh thu rất lớn nên khai thuế hằng tháng."
                    : "Mỗi quý khai một lần, hạn là cuối tháng đầu của quý sau.",
                Todo, null),
            new(
                "EINVOICE",
                "Dùng hoá đơn điện tử",
                $"Doanh thu trên {TaxFormat.Money(policy.EInvoiceThreshold)}/năm phải dùng hoá đơn điện tử có mã của " +
                "cơ quan thuế hoặc hoá đơn từ máy tính tiền kết nối với cơ quan thuế.",
                Todo, eInvoiceRegistered),
            new(
                "PAY_TAX",
                "Nộp thuế theo số đã khai",
                "Thuế GTGT và TNCN tính theo tỷ lệ trên doanh thu của ngành nghề. Xem mục Ước tính thuế để biết số tham khảo.",
                Todo, null),
        };

        if (group == TaxGroups.Three)
        {
            list.Add(new TaxObligation(
                "PROFIT_METHOD",
                "Tính thuế TNCN theo lợi nhuận",
                "Nhóm này tính TNCN trên doanh thu trừ chi phí nên cần sổ sách và chứng từ chi phí.",
                Todo, null));
        }

        return list;
    }

    private static TaxObligation TaxCode(bool hasTaxCode) => new(
        "REGISTER_TAX_CODE",
        "Có mã số thuế",
        "Hộ kinh doanh dùng số định danh cá nhân làm mã số thuế. Nhập vào Hồ sơ thuế để phường đối chiếu được.",
        Todo, hasTaxCode);
}

// ---------------------------------------------------------------------------------------------

public static class HouseholdStatuses
{
    public const string Active = "ACTIVE";
    public const string Suspended = "SUSPENDED";
    public const string Ended = "ENDED";
    public const string NotStarted = "NOT_STARTED";

    /// <summary>
    /// A household is ACTIVE while it holds a contract in force with a live permit, SUSPENDED while
    /// its permit or contract is suspended, ENDED once every contract it ever had is over, and
    /// NOT_STARTED when it is approved but never rented a slot.
    /// </summary>
    public static string Of(WardHouseholdFacts facts)
    {
        if (facts.TotalContracts == 0)
        {
            return NotStarted;
        }

        if (facts.ActiveContracts > 0)
        {
            return facts.PermitStatus == "SUSPENDED" ? Suspended : Active;
        }

        return facts.SuspendedContracts > 0 ? Suspended : Ended;
    }
}

public static class TaxReferralReasons
{
    public const string UnregisteredActivity = "UNREGISTERED_ACTIVITY";
    public const string InvalidPermitActivity = "INVALID_PERMIT_ACTIVITY";
    public const string AddressChanged = "ADDRESS_CHANGED";
    public const string BusinessEnded = "BUSINESS_ENDED";
    public const string NewHousehold = "NEW_HOUSEHOLD";

    public static string Label(string code) => code switch
    {
        UnregisteredActivity => "Kinh doanh không có giấy phép hoặc chưa đăng ký",
        InvalidPermitActivity => "Kinh doanh khi giấy phép không hợp lệ",
        AddressChanged => "Đổi địa điểm kinh doanh",
        BusinessEnded => "Ngừng kinh doanh tại địa điểm",
        NewHousehold => "Hộ mới, chưa có mã số thuế",
        _ => code,
    };
}

public static class TaxReferralStatuses
{
    public const string Pending = "PENDING";
    public const string Reported = "REPORTED";
    public const string Dismissed = "DISMISSED";

    public static bool IsValid(string? value) => value is Pending or Reported or Dismissed;
}

public static class TaxAnnouncementSegments
{
    public const string All = "ALL";
    public const string NotFiled = "NOT_FILED";
    public const string NoTaxCode = "NO_TAX_CODE";

    public static bool IsValid(string? value) => value is All or NotFiled or NoTaxCode;
}

public static class TaxCodes
{
    /// <summary>Mã số thuế: 10 digits, 10 digits-3 digits, 13 digits, or the 12-digit personal id.</summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length switch
        {
            10 => value.All(char.IsAsciiDigit),
            12 => value.All(char.IsAsciiDigit),
            13 => value.All(char.IsAsciiDigit),
            14 => value[10] == '-' && value[..10].All(char.IsAsciiDigit) && value[11..].All(char.IsAsciiDigit),
            _ => false,
        };

    /// <summary>"••••••1234": enough for the ward to recognise it, not to copy it.</summary>
    public static string Mask(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= 4 ? "" : new string('•', value.Length - 4) + value[^4..];
}
