using System.Text;
using ClosedXML.Excel;
using StreetBiz.Application.Common.Models;
using StreetBiz.Infrastructure.Documents;

namespace StreetBiz.Infrastructure.Tests;

public sealed class TaxDocumentRendererTests
{
    private static readonly DateTime GeneratedAt = new(2026, 10, 18, 3, 0, 0, DateTimeKind.Utc);

    private static TaxBookExport Book(string? taxCode = "048090001234") => new(
        2026, "Phạm Thị Lan", "Bánh mì Cô Lan", taxCode, "Dịch vụ ăn uống", GeneratedAt,
        1_000_000_000m, "G2", "từ 500 triệu đến 3 tỷ", "NEAR", 600_000_000m, 250_000_000m,
        [
            new TaxBookMonth(1, 100_000_000m, 50_000_000m),
            new TaxBookMonth(2, 200_000_000m, 100_000_000m),
            new TaxBookMonth(5, 300_000_000m, 100_000_000m),
        ],
        [
            new TaxBookDay(new DateOnly(2026, 10, 17), 100_000m, 1, 0m, ""),
            new TaxBookDay(new DateOnly(2026, 10, 18), 150_000m, 2, 320_000m, "CASH 300.000 · BANK 20.000"),
        ],
        [
            new TaxBookEntry(new DateOnly(2026, 10, 18), 300_000m, "CASH", "Bán tại chỗ", GeneratedAt, false, null),
            new TaxBookEntry(new DateOnly(2026, 10, 17), 500_000m, "CASH", null, GeneratedAt, true, "Nhập nhầm"),
        ],
        [
            new TaxBookFiling("Thông báo doanh thu năm 2026", new DateOnly(2027, 1, 31), "FILED", 840_000_000m, new DateOnly(2027, 1, 20), "BN-2027-0001"),
            new TaxBookFiling("Quý 3/2026", new DateOnly(2026, 10, 31), "DUE_SOON", null, null, null),
        ]);

    [Fact]
    public void The_revenue_book_workbook_has_the_four_sheets_with_totals_as_formulas()
    {
        var bytes = new TaxDocumentRenderer().RenderRevenueBookXlsx(Book());

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(["Tổng hợp", "Sổ doanh thu", "Khoản đã ghi", "Kê khai"], workbook.Worksheets.Select(sheet => sheet.Name));

        var summary = workbook.Worksheet("Tổng hợp");
        Assert.Equal("SỔ DOANH THU NĂM 2026", summary.Cell("A1").GetString());
        Assert.Equal("Phạm Thị Lan · Bánh mì Cô Lan", summary.Cell("A2").GetString());
        Assert.Contains("048090001234", summary.Cell("A3").GetString());
        Assert.Equal(600_000_000d, summary.Cell("B7").GetDouble());
        Assert.Equal(250_000_000d, summary.Cell("B8").GetDouble());
        Assert.Equal(850_000_000d, summary.Cell("B9").GetDouble()); // SUM of the two lines
        Assert.Equal("Gần ngưỡng không chịu thuế", summary.Cell("B13").GetString());
        // Monthly table: three months and a "Cả năm" row summing them.
        Assert.Equal("Tháng 1", summary.Cell("A16").GetString());
        Assert.Equal("Cả năm", summary.Cell("A19").GetString());
        Assert.Equal(600_000_000d, summary.Cell("B19").GetDouble());
        Assert.Equal(850_000_000d, summary.Cell("D19").GetDouble());
        Assert.Contains(workbook.Worksheet("Tổng hợp").CellsUsed(), cell => cell.GetString().Contains("tham khảo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Each_day_is_a_row_and_the_totals_row_adds_them_up()
    {
        var bytes = new TaxDocumentRenderer().RenderRevenueBookXlsx(Book());

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var days = workbook.Worksheet("Sổ doanh thu");
        Assert.Equal(new DateTime(2026, 10, 17), days.Cell("A2").GetDateTime());
        Assert.Equal(100_000d, days.Cell("E2").GetDouble());
        Assert.Equal(470_000d, days.Cell("E3").GetDouble()); // 150.000 + 320.000
        Assert.Equal("Tổng cộng", days.Cell("A4").GetString());
        Assert.Equal(250_000d, days.Cell("B4").GetDouble());
        Assert.Equal(3d, days.Cell("C4").GetDouble());
        Assert.Equal(320_000d, days.Cell("D4").GetDouble());
        Assert.Equal(570_000d, days.Cell("E4").GetDouble());
    }

    [Fact]
    public void An_empty_book_still_makes_a_valid_workbook()
    {
        var empty = Book() with { Online = 0, Recorded = 0, Months = [], Days = [], Entries = [], Filings = [], Level = "SAFE", TaxCode = null };

        var bytes = new TaxDocumentRenderer().RenderRevenueBookXlsx(empty);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        Assert.Contains("chưa khai", workbook.Worksheet("Tổng hợp").Cell("A3").GetString());
        Assert.Equal("Tổng cộng", workbook.Worksheet("Sổ doanh thu").Cell("A2").GetString()); // only the totals row, no reversed SUM range
        Assert.Equal("0", workbook.Worksheet("Sổ doanh thu").Cell("E2").FormulaA1);
        Assert.Equal("0", workbook.Worksheet("Tổng hợp").Cell("D16").FormulaA1);
    }

    [Fact]
    public void Voided_entries_stay_in_the_book_but_are_marked_as_not_counted()
    {
        var bytes = new TaxDocumentRenderer().RenderRevenueBookXlsx(Book());

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var entries = workbook.Worksheet("Khoản đã ghi");
        Assert.Equal("Hiệu lực", entries.Cell("F2").GetString());
        Assert.Equal("Đã huỷ (không tính)", entries.Cell("F3").GetString());
        Assert.Equal("Nhập nhầm", entries.Cell("G3").GetString());
        Assert.True(entries.Cell("B3").Style.Font.Strikethrough);
        Assert.False(entries.Cell("B2").Style.Font.Strikethrough);
    }

    [Fact]
    public void Filings_show_status_amount_date_and_receipt_with_the_self_confirmed_note()
    {
        var bytes = new TaxDocumentRenderer().RenderRevenueBookXlsx(Book());

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var filings = workbook.Worksheet("Kê khai");
        Assert.Equal("Đã nộp", filings.Cell("C2").GetString());
        Assert.Equal(840_000_000d, filings.Cell("D2").GetDouble());
        Assert.Equal(new DateTime(2027, 1, 20), filings.Cell("E2").GetDateTime());
        Assert.Equal("BN-2027-0001", filings.Cell("F2").GetString());
        Assert.Equal("Sắp đến hạn", filings.Cell("C3").GetString());
        Assert.True(filings.Cell("D3").IsEmpty());
        Assert.Contains("không khai hay nộp thay", filings.Cell("A5").GetString());
    }

    [Fact]
    public void The_yearly_summary_renders_as_a_PDF_with_or_without_filings()
    {
        var renderer = new TaxDocumentRenderer();

        foreach (var book in new[] { Book(), Book(null) with { Filings = [], BusinessName = null } })
        {
            var pdf = renderer.RenderRevenueSummaryPdf(book, GeneratedAt);
            Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
            Assert.True(pdf.Length > 5_000, "the embedded font alone makes a real document several KB");
        }
    }

    // --------------------------------------------------------------------------------- ward

    private static WardTaxExport WardExport() => new(
        2026, "Phường Hải Châu 1", GeneratedAt,
        [("Số hộ kinh doanh", "2"), ("Đã thông báo doanh thu năm", "1")],
        [
            new WardTaxExportHousehold(
                "Bánh mì Cô Lan", "Phạm Thị Lan", "0905000101", "123 Nguyễn Văn Linh", "Ăn uống", "ACTIVE", "NVL-01, NVL-02",
                GeneratedAt.AddDays(-20), GeneratedAt.AddDays(-2), "••••••••1234", true, 850_000_000m, "G2"),
            new WardTaxExportHousehold(
                "Xe nước ép", "Hoàng Văn Tám", null, null, null, "ENDED", "",
                GeneratedAt.AddDays(-10), null, "Chưa khai", false, null, null),
        ],
        [
            new WardTaxExportReferral(
                "Hoạt động chưa đăng ký", "Biên bản 0001: kinh doanh không phép tại ô NVL-01.", null, GeneratedAt.AddDays(-3),
                "PENDING", null, null, null),
            new WardTaxExportReferral(
                "Đổi địa điểm", "Hộ Bánh mì Cô Lan đổi địa điểm.", "Bánh mì Cô Lan", GeneratedAt.AddDays(-6),
                "REPORTED", GeneratedAt, "CV-12/PHƯỜNG", "Gửi thuế cơ sở"),
        ]);

    [Fact]
    public void The_ward_workbook_lists_households_and_referrals()
    {
        var bytes = new TaxDocumentRenderer().RenderWardTaxXlsx(WardExport());

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(["Tổng hợp", "Hộ kinh doanh", "Đề nghị cơ quan thuế"], workbook.Worksheets.Select(sheet => sheet.Name));
        var summary = workbook.Worksheet("Tổng hợp");
        Assert.Equal("UBND Phường Hải Châu 1", summary.Cell("A2").GetString());
        Assert.Equal("Số hộ kinh doanh", summary.Cell("A6").GetString());
        Assert.Equal("2", summary.Cell("B6").GetString());

        var households = workbook.Worksheet("Hộ kinh doanh");
        Assert.Equal("Bánh mì Cô Lan", households.Cell("B2").GetString());
        Assert.Equal("0905000101", households.Cell("D2").GetString()); // kept as text so the leading zero survives
        Assert.Equal("Đang hoạt động", households.Cell("G2").GetString());
        Assert.Equal("NVL-01, NVL-02", households.Cell("H2").GetString());
        Assert.Equal("Đã thông báo", households.Cell("L2").GetString());
        Assert.Equal("Đã ngừng", households.Cell("G3").GetString());

        var referrals = workbook.Worksheet("Đề nghị cơ quan thuế");
        Assert.Equal("Chưa xác định", referrals.Cell("C2").GetString());
        Assert.Equal("Chờ xử lý", referrals.Cell("F2").GetString());
        Assert.Equal("Đã báo cơ quan thuế", referrals.Cell("F3").GetString());
        Assert.Equal("CV-12/PHƯỜNG", referrals.Cell("H3").GetString());
    }

    [Fact]
    public void A_household_that_did_not_agree_to_share_shows_no_revenue_and_no_group()
    {
        var bytes = new TaxDocumentRenderer().RenderWardTaxXlsx(WardExport());

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var households = workbook.Worksheet("Hộ kinh doanh");
        Assert.Equal(850_000_000d, households.Cell("M2").GetDouble());
        Assert.Equal("Nhóm 2", households.Cell("N2").GetString());
        Assert.Equal("Không chia sẻ", households.Cell("M3").GetString());
        Assert.True(households.Cell("N3").IsEmpty());
        // Nothing of the second household's takings anywhere in the sheet, only the consent notice.
        Assert.DoesNotContain(households.CellsUsed(), cell => cell.Address.RowNumber == 3 && cell.DataType == XLDataType.Number && cell.Address.ColumnNumber is 13 or 14);
        Assert.Equal("••••••••1234", households.Cell("K2").GetString()); // the tax code arrives masked
    }
}
