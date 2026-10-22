using System.Text;
using ClosedXML.Excel;
using StreetBiz.Application.Common.Models;
using StreetBiz.Infrastructure.Documents;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>ADM-07 files: they open again, keep the header when empty, and never run user text as a formula.</summary>
public sealed class PlatformReportRendererTests
{
    private static readonly PlatformReportRenderer Renderer = new();

    private static PlatformReportDataset Sample(params object?[][] rows) => new(
        "Báo cáo thử",
        [new ReportSheet("Đơn hàng", ["Mã đơn", "Tổng tiền (đ)", "Ghi chú"], rows.Select(r => (IReadOnlyList<object?>)r).ToArray())]);

    [Fact]
    public void The_workbook_opens_again_with_headers_text_and_numbers()
    {
        var bytes = Renderer.RenderXlsx(Sample(["DH-1", 125_000m, "Giao nhanh"], ["DH-2", 40_000m, null]));

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheet("Đơn hàng");
        Assert.Equal("Mã đơn", sheet.Cell(1, 1).GetString());
        Assert.Equal("DH-1", sheet.Cell(2, 1).GetString());
        Assert.Equal(125_000d, sheet.Cell(2, 2).GetDouble());
        Assert.Equal("Giao nhanh", sheet.Cell(2, 3).GetString());
        Assert.True(sheet.Cell(3, 3).IsEmpty());
        Assert.Equal(3, sheet.LastRowUsed()!.RowNumber());
    }

    [Fact]
    public void An_empty_period_still_produces_a_file_with_its_header_row()
    {
        var bytes = Renderer.RenderXlsx(Sample());

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheet("Đơn hàng");
        Assert.Equal(1, sheet.LastRowUsed()!.RowNumber());
        Assert.Equal("Tổng tiền (đ)", sheet.Cell(1, 2).GetString());
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    public void Text_that_looks_like_a_formula_is_stored_as_text(string hostile)
    {
        var bytes = Renderer.RenderXlsx(Sample(["DH-1", 1m, hostile]));

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var cell = workbook.Worksheet("Đơn hàng").Cell(2, 3);
        Assert.False(cell.HasFormula);
        Assert.Equal(XLDataType.Text, cell.DataType);
        // ClosedXML turns the leading apostrophe into Excel's own "quote prefix" flag: the text is shown
        // exactly as typed and is never evaluated.
        Assert.True(cell.Style.IncludeQuotePrefix);
        Assert.Equal(hostile, cell.GetString());
    }

    [Fact]
    public void Several_tables_become_several_sheets_with_valid_unique_names()
    {
        var dataset = new PlatformReportDataset(
            "Nhiều bảng",
            [
                new ReportSheet("A/B:C*?[x]", ["h"], []),
                new ReportSheet("ABC x", ["h"], []),
                new ReportSheet(new string('t', 60), ["h"], []),
            ]);

        using var workbook = new XLWorkbook(new MemoryStream(Renderer.RenderXlsx(dataset)));

        Assert.Equal(3, workbook.Worksheets.Count);
        Assert.All(workbook.Worksheets, sheet => Assert.InRange(sheet.Name.Length, 1, 31));
        Assert.Equal(workbook.Worksheets.Count, workbook.Worksheets.Select(s => s.Name.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void The_csv_starts_with_a_byte_order_mark_and_quotes_what_needs_quoting()
    {
        var bytes = Renderer.RenderCsv(Sample(["DH-1", 125_000.5m, "có, dấu phẩy và \"ngoặc\""], ["DH-2", -5m, null]));

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Mã đơn,Tổng tiền (đ),Ghi chú", lines[0]);
        Assert.Equal("DH-1,125000.5,\"có, dấu phẩy và \"\"ngoặc\"\"\"", lines[1]);
        // A real negative number stays a number; only text is neutralised.
        Assert.Equal("DH-2,-5,", lines[2]);
    }

    [Fact]
    public void The_csv_neutralises_formula_text()
    {
        var bytes = Renderer.RenderCsv(Sample(["DH-1", 1m, "=cmd|' /C calc'!A0"]));

        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("'=cmd|' /C calc'!A0", text);
        Assert.DoesNotContain(",=cmd", text);
    }

    [Fact]
    public void A_csv_with_several_tables_titles_each_section()
    {
        var dataset = new PlatformReportDataset(
            "Hai bảng",
            [new ReportSheet("Một", ["a"], [["1"]]), new ReportSheet("Hai", ["b"], [["2"]])]);

        var lines = Encoding.UTF8.GetString(Renderer.RenderCsv(dataset)).TrimStart('﻿')
            .Split("\r\n");

        Assert.Equal(["Một", "a", "1", "", "Hai", "b", "2", ""], lines);
    }
}
