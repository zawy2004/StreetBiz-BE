using System.Text;
using ClosedXML.Excel;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.DTOs.Finance;
using StreetBiz.Infrastructure.Documents;

namespace StreetBiz.Infrastructure.Tests;

public sealed class FinanceDocumentRendererTests
{
    private static readonly DateTime PaidAt = new(2026, 9, 26, 3, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void A_receipt_renders_as_a_PDF_for_both_fees_and_penalties()
    {
        var renderer = new FinanceDocumentRenderer();
        var fee = new InvoiceDocumentRow(
            "HD-2026-000004", "FEE", 1_040_000m, PaidAt, "Phường Hải Châu 1", "Phạm Thị Lan", "0905000101",
            "Bánh mì & Xôi Cô Lan", "NVL-01", "Kỳ 3/3 · Tháng 10/2026", null, null, "MOMO", "SB-T9-a5f0b068", PaidAt);
        var penalty = fee with
        {
            InvoiceNumber = "HD-2026-000005", Kind = "PENALTY", Amount = 1_000_000m, PeriodLabel = null,
            ViolationLabel = "Cản trở lối đi bộ dành cho người đi đường", DecisionNumber = "QĐXP-2026/0003",
        };

        foreach (var row in new[] { fee, penalty })
        {
            var pdf = renderer.RenderInvoicePdf(row, PaidAt);
            Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
            Assert.True(pdf.Length > 5_000, "the embedded font alone makes a real receipt several KB");
        }
    }

    [Fact]
    public void The_workbook_carries_the_totals_the_receipts_and_a_formula_total()
    {
        var report = new CollectionReportRow(
            3_670_000m, 4_950_000m, 2_680_000m, 1_500_000m, 0m, 2,
            [new RecentViolationRow(2, "BLOCK_PEDESTRIAN", "Cản trở lối đi bộ", "Phạm Thị Lan", "NVL-08", 1_000_000m, "UNPAID", PaidAt)]);
        WardInvoiceRow[] receipts =
        [
            new("HD-2026-000004", PaidAt, "FEE", "Phạm Thị Lan", "NVL-01", "Phí thuê ô NVL-01 — Kỳ 3/3 · Tháng 10/2026", 1_040_000m, "MOMO"),
            new("HD-2026-000005", PaidAt.AddHours(1), "PENALTY", "Phạm Thị Lan", "NVL-08", "Tiền phạt: Cản trở lối đi bộ", 1_000_000m, null),
        ];

        var bytes = new FinanceDocumentRenderer().RenderCollectionReportXlsx(new CollectionReportExport(
            "Phường Hải Châu 1", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 27), report, receipts, PaidAt));

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        // No performance computed for this export, so no per-zone sheet; the debtor sheet is always there.
        Assert.Equal(["Tổng hợp", "Bảng kê thu", "Nợ phí", "Vi phạm gần đây"], workbook.Worksheets.Select(sheet => sheet.Name));

        var summary = workbook.Worksheet("Tổng hợp");
        Assert.Equal("Phường Hải Châu 1", summary.Cell("A2").GetString());
        Assert.Equal("Kỳ báo cáo: 01/09/2026 – 27/09/2026", summary.Cell("A3").GetString());
        Assert.Equal(3_670_000d, summary.Cell("B7").GetDouble());
        Assert.Equal(5_170_000d, summary.Cell("B9").GetDouble()); // SUM of the two collected lines

        var list = workbook.Worksheet("Bảng kê thu");
        Assert.Equal("HD-2026-000004", list.Cell("B2").GetString());
        Assert.Equal("Phí thuê ô", list.Cell("D2").GetString());
        Assert.Equal("MoMo", list.Cell("H2").GetString());
        Assert.Equal("Tiền phạt", list.Cell("D3").GetString());
        Assert.Equal("Tổng cộng", list.Cell("H4").GetString());
        Assert.Equal("SUM(I2:I3)", list.Cell("I4").FormulaA1);
        Assert.Equal(2_040_000d, list.Cell("I4").GetDouble());
        // Đà Nẵng time: 03:30 UTC is 10:30 on the same day.
        Assert.Equal(new DateTime(2026, 9, 26, 10, 30, 0), list.Cell("C2").GetDateTime());

        Assert.Equal("Chưa nộp phạt", workbook.Worksheet("Vi phạm gần đây").Cell("F2").GetString());
    }

    [Fact]
    public void The_workbook_lists_debtors_zones_and_the_on_time_rate_when_given()
    {
        var report = new CollectionReportRow(0m, 0m, 2_080_000m, 0m, 0m, 0, []);
        var export = new CollectionReportExport(
            "Phường Hải Châu 1", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), report, [], PaidAt)
        {
            Debtors =
            [
                new WardDebtorDto(3, "Phạm Thị Lan", "0905000101", "Bánh mì Cô Lan", "NVL-01", "Nguyễn Văn Linh",
                    2, 2_080_000m, 1_040_000m, new DateOnly(2026, 8, 20), 38, PaidAt, false),
            ],
            Performance = new CollectionPerformanceDto(
                new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 3_120_000m, 3, 1_040_000m, 1, 0, 2, 1 / 3d,
                [new ZoneCollectionDto(1, "Nguyễn Văn Linh", 12, 8, 1_040_000m, 2_080_000m)]),
        };

        using var workbook = new XLWorkbook(new MemoryStream(new FinanceDocumentRenderer().RenderCollectionReportXlsx(export)));

        Assert.Equal(["Tổng hợp", "Bảng kê thu", "Nợ phí", "Theo khu vực", "Vi phạm gần đây"],
            workbook.Worksheets.Select(sheet => sheet.Name));
        var debtors = workbook.Worksheet("Nợ phí");
        Assert.Equal("Bánh mì Cô Lan", debtors.Cell("B2").GetString());
        Assert.Equal(2_080_000d, debtors.Cell("H2").GetDouble());
        Assert.Equal(38, debtors.Cell("K2").GetDouble());
        Assert.Equal("SUM(H2:H2)", debtors.Cell("H3").FormulaA1);
        Assert.Equal(8, workbook.Worksheet("Theo khu vực").Cell("C2").GetDouble());

        var summary = workbook.Worksheet("Tổng hợp");
        var rateRow = summary.Column(1).CellsUsed().Single(cell => cell.GetString() == "Tỷ lệ nộp đúng hạn").Address.RowNumber;
        Assert.Equal(1 / 3d, summary.Cell(rateRow, 2).GetDouble(), 4);
        Assert.Equal("0.0%", summary.Cell(rateRow, 2).Style.NumberFormat.Format);
    }

    [Fact]
    public void A_vendor_statement_lists_the_years_receipts_and_instalments()
    {
        var statement = new VendorStatementExport(
            "Phạm Thị Lan",
            "Bánh mì Cô Lan",
            2026,
            [
                new InvoiceRow(4, "HD-2026-000004", "FEE", 1_040_000m, PaidAt, 3, null, "Kỳ 3/3 · Tháng 10/2026", "NVL-01", null, "MOMO", PaidAt),
                new InvoiceRow(5, "HD-2026-000005", "PENALTY", 500_000m, PaidAt, null, 1, null, "NVL-01", "Cản trở lối đi bộ", "ZALOPAY", PaidAt),
            ],
            [
                new StatementInstalmentRow("NVL-01", "Kỳ 3/3 · Tháng 10/2026", new DateOnly(2026, 10, 2), 1_040_000m, "PAID", PaidAt, "HD-2026-000004"),
                new StatementInstalmentRow("NVL-01", "Kỳ 4/4 · Tháng 11/2026", new DateOnly(2026, 11, 1), 1_040_000m, "PENDING", null, null),
            ],
            PaidAt);

        using var workbook = new XLWorkbook(new MemoryStream(new FinanceDocumentRenderer().RenderVendorStatementXlsx(statement)));

        Assert.Equal(["Tổng hợp", "Chứng từ", "Lịch phí"], workbook.Worksheets.Select(sheet => sheet.Name));
        var summary = workbook.Worksheet("Tổng hợp");
        Assert.Equal("SAO KÊ PHÍ THUÊ VỈA HÈ VÀ TIỀN PHẠT NĂM 2026", summary.Cell("A1").GetString());
        Assert.Equal(1_040_000d, summary.Cell("B6").GetDouble()); // fees paid
        Assert.Equal(500_000d, summary.Cell("B7").GetDouble());   // penalties paid
        Assert.Equal(1_040_000d, summary.Cell("B10").GetDouble()); // still owed
        Assert.Equal("SUM(H2:H3)", workbook.Worksheet("Chứng từ").Cell("H4").FormulaA1);
        Assert.Equal("Chưa đến hạn", workbook.Worksheet("Lịch phí").Cell("E3").GetString());
    }
}
