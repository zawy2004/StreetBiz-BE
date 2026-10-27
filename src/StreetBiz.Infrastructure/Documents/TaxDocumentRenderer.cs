using System.Globalization;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.Tax;

namespace StreetBiz.Infrastructure.Documents;

/// <summary>
/// The household tax documents: the revenue book (.xlsx), the yearly summary (PDF) and the ward's
/// household and referral workbook (.xlsx). Every timestamp is shown in Đà Nẵng time; the PDF uses
/// the embedded Be Vietnam Pro font (shared with the finance documents) so diacritics never depend on
/// what the server has installed. None of these is a tax form: each says so.
/// </summary>
public sealed class TaxDocumentRenderer : ITaxDocumentRenderer
{
    private const string Font = "Be Vietnam Pro";
    private const string MoneyFormat = "#,##0";
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");

    // ------------------------------------------------------------------------ revenue book

    public byte[] RenderRevenueBookXlsx(TaxBookExport book)
    {
        using var workbook = new XLWorkbook();
        WriteBookSummary(workbook.Worksheets.Add("Tổng hợp"), book);
        WriteBookDays(workbook.Worksheets.Add("Sổ doanh thu"), book);
        WriteBookEntries(workbook.Worksheets.Add("Khoản đã ghi"), book.Entries);
        WriteBookFilings(workbook.Worksheets.Add("Kê khai"), book.Filings);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteBookSummary(IXLWorksheet sheet, TaxBookExport book)
    {
        sheet.Cell("A1").Value = $"SỔ DOANH THU NĂM {book.TaxYear}";
        sheet.Cell("A1").Style.Font.SetBold().Font.SetFontSize(14);
        sheet.Cell("A2").Value = book.BusinessName is { Length: > 0 } business
            ? $"{book.VendorName} · {business}"
            : book.VendorName;
        sheet.Cell("A3").Value = $"Mã số thuế: {(string.IsNullOrWhiteSpace(book.TaxCode) ? "chưa khai" : book.TaxCode)} · Ngành: {book.IndustryName}";
        sheet.Cell("A4").Value = $"Lập lúc {Local(book.GeneratedAt):HH:mm dd/MM/yyyy}";

        Header(sheet.Row(6), "Chỉ tiêu", "Giá trị");
        var row = 7;
        Line(sheet, ref row, "Doanh thu từ đơn hàng StreetBiz (đã trừ hoàn tiền)", book.Online);
        Line(sheet, ref row, "Doanh thu bán trực tiếp do hộ ghi", book.Recorded);
        sheet.Cell(row, 1).Value = "Tổng doanh thu năm";
        sheet.Cell(row, 2).FormulaA1 = "SUM(B7:B8)";
        sheet.Cell(row, 2).Style.NumberFormat.Format = MoneyFormat;
        sheet.Range(row, 1, row, 2).Style.Font.SetBold();
        row += 2;
        Line(sheet, ref row, "Ngưỡng doanh thu không chịu thuế", book.Threshold);
        sheet.Cell(row, 1).Value = "Nhóm doanh thu";
        sheet.Cell(row++, 2).Value = $"{GroupLabel(book.Group)}: {book.GroupDescription}";
        sheet.Cell(row, 1).Value = "Mức so với ngưỡng";
        sheet.Cell(row++, 2).Value = LevelLabel(book.Level);

        row++;
        Header(sheet.Row(row), "Tháng", "Đơn hàng StreetBiz (đ)", "Bán trực tiếp (đ)", "Tổng (đ)");
        var first = row + 1;
        foreach (var month in book.Months)
        {
            row++;
            sheet.Cell(row, 1).Value = $"Tháng {month.Month}";
            Money(sheet.Cell(row, 2), month.Online);
            Money(sheet.Cell(row, 3), month.Recorded);
            sheet.Cell(row, 4).FormulaA1 = $"B{row}+C{row}";
            sheet.Cell(row, 4).Style.NumberFormat.Format = MoneyFormat;
        }

        row++;
        sheet.Cell(row, 1).Value = "Cả năm";
        foreach (var column in new[] { "B", "C", "D" })
        {
            sheet.Cell($"{column}{row}").FormulaA1 = book.Months.Count == 0 ? "0" : $"SUM({column}{first}:{column}{row - 1})";
            sheet.Cell($"{column}{row}").Style.NumberFormat.Format = MoneyFormat;
        }

        sheet.Range(row, 1, row, 4).Style.Font.SetBold();

        row += 2;
        sheet.Cell(row, 1).Value = TaxMessages.Disclaimer;
        sheet.Cell(row, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);
        sheet.Column(1).Width = 52;
        sheet.Column(2).Width = 26;
        sheet.Column(3).Width = 20;
        sheet.Column(4).Width = 18;
    }

    private static void Line(IXLWorksheet sheet, ref int row, string label, decimal amount)
    {
        sheet.Cell(row, 1).Value = label;
        Money(sheet.Cell(row, 2), amount);
        row++;
    }

    private static void WriteBookDays(IXLWorksheet sheet, TaxBookExport book)
    {
        Header(sheet.Row(1), "Ngày", "Đơn hàng StreetBiz (đ)", "Số đơn", "Bán trực tiếp (đ)", "Tổng (đ)", "Ghi chú");
        var row = 2;
        foreach (var day in book.Days)
        {
            sheet.Cell(row, 1).Value = day.Date.ToDateTime(TimeOnly.MinValue);
            sheet.Cell(row, 1).Style.DateFormat.Format = "dd/MM/yyyy";
            Money(sheet.Cell(row, 2), day.Online);
            sheet.Cell(row, 3).Value = day.OnlineOrders;
            Money(sheet.Cell(row, 4), day.Recorded);
            sheet.Cell(row, 5).FormulaA1 = $"B{row}+D{row}";
            sheet.Cell(row, 5).Style.NumberFormat.Format = MoneyFormat;
            sheet.Cell(row, 6).Value = day.Remarks;
            row++;
        }

        sheet.Cell(row, 1).Value = "Tổng cộng";
        foreach (var column in new[] { "B", "C", "D", "E" })
        {
            sheet.Cell($"{column}{row}").FormulaA1 = book.Days.Count == 0 ? "0" : $"SUM({column}2:{column}{row - 1})";
            if (column != "C")
            {
                sheet.Cell($"{column}{row}").Style.NumberFormat.Format = MoneyFormat;
            }
        }

        sheet.Range(row, 1, row, 6).Style.Font.SetBold();
        sheet.SheetView.FreezeRows(1);
        double[] widths = [13, 22, 9, 18, 16, 52];
        for (var column = 0; column < widths.Length; column++)
        {
            sheet.Column(column + 1).Width = widths[column];
        }
    }

    private static void WriteBookEntries(IXLWorksheet sheet, IReadOnlyList<TaxBookEntry> entries)
    {
        Header(sheet.Row(1), "Ngày bán", "Số tiền (đ)", "Hình thức", "Ghi chú", "Ghi lúc", "Tình trạng", "Lý do huỷ");
        var row = 2;
        foreach (var entry in entries)
        {
            sheet.Cell(row, 1).Value = entry.Date.ToDateTime(TimeOnly.MinValue);
            sheet.Cell(row, 1).Style.DateFormat.Format = "dd/MM/yyyy";
            Money(sheet.Cell(row, 2), entry.Amount);
            sheet.Cell(row, 3).Value = entry.Channel;
            sheet.Cell(row, 4).Value = entry.Note ?? "";
            sheet.Cell(row, 5).Value = Local(entry.CreatedAt);
            sheet.Cell(row, 5).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
            sheet.Cell(row, 6).Value = entry.Voided ? "Đã huỷ (không tính)" : "Hiệu lực";
            sheet.Cell(row, 7).Value = entry.VoidReason ?? "";
            if (entry.Voided)
            {
                sheet.Range(row, 1, row, 7).Style.Font.SetFontColor(XLColor.Gray).Font.SetStrikethrough();
            }

            row++;
        }

        sheet.SheetView.FreezeRows(1);
        double[] widths = [13, 16, 15, 40, 17, 20, 30];
        for (var column = 0; column < widths.Length; column++)
        {
            sheet.Column(column + 1).Width = widths[column];
        }
    }

    private static void WriteBookFilings(IXLWorksheet sheet, IReadOnlyList<TaxBookFiling> filings)
    {
        Header(sheet.Row(1), "Kỳ", "Hạn", "Tình trạng", "Doanh thu đã khai (đ)", "Ngày nộp", "Mã biên nhận");
        var row = 2;
        foreach (var filing in filings)
        {
            sheet.Cell(row, 1).Value = filing.Label;
            sheet.Cell(row, 2).Value = filing.DueDate.ToDateTime(TimeOnly.MinValue);
            sheet.Cell(row, 2).Style.DateFormat.Format = "dd/MM/yyyy";
            sheet.Cell(row, 3).Value = PeriodStatusLabel(filing.Status);
            if (filing.RevenueDeclared is { } declared)
            {
                Money(sheet.Cell(row, 4), declared);
            }

            if (filing.FiledOn is { } filedOn)
            {
                sheet.Cell(row, 5).Value = filedOn.ToDateTime(TimeOnly.MinValue);
                sheet.Cell(row, 5).Style.DateFormat.Format = "dd/MM/yyyy";
            }

            sheet.Cell(row, 6).Value = filing.AuthorityReference ?? "";
            row++;
        }

        sheet.Cell(row + 1, 1).Value = "Hộ tự xác nhận đã nộp trên cổng của cơ quan thuế; StreetBiz không khai hay nộp thay.";
        sheet.Cell(row + 1, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);
        double[] widths = [34, 13, 22, 24, 13, 26];
        for (var column = 0; column < widths.Length; column++)
        {
            sheet.Column(column + 1).Width = widths[column];
        }
    }

    // ------------------------------------------------------------------------- yearly summary

    public byte[] RenderRevenueSummaryPdf(TaxBookExport book, DateTime printedAtUtc)
    {
        FinanceDocumentRenderer.EnsureFonts();
        var quarters = Enumerable.Range(1, 4)
            .Select(quarter => (
                Quarter: quarter,
                Online: book.Months.Where(m => (m.Month - 1) / 3 + 1 == quarter).Sum(m => m.Online),
                Recorded: book.Months.Where(m => (m.Month - 1) / 3 + 1 == quarter).Sum(m => m.Recorded)))
            .ToList();

        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(36);
            page.DefaultTextStyle(style => style.FontFamily(Font).FontSize(9.5f).FontColor(Colors.Grey.Darken4));

            page.Header().Column(column =>
            {
                column.Item().Text("Nền tảng quản lý vỉa hè StreetBiz").FontSize(8).FontColor(Colors.Grey.Darken1);
                column.Item().PaddingTop(6).Text($"BẢN TỔNG HỢP DOANH THU NĂM {book.TaxYear}").FontSize(16).Bold();
                column.Item().Text("Số liệu để đối chiếu khi thông báo doanh thu cho cơ quan thuế. Không phải tờ khai thuế.")
                    .FontColor(Colors.Grey.Darken1);
            });

            page.Content().PaddingVertical(14).Column(column =>
            {
                column.Spacing(10);

                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(110);
                        columns.RelativeColumn();
                    });
                    Row(table, "Chủ hộ", book.VendorName);
                    Row(table, "Hộ kinh doanh", book.BusinessName);
                    Row(table, "Mã số thuế", string.IsNullOrWhiteSpace(book.TaxCode) ? "Chưa khai trên StreetBiz" : book.TaxCode);
                    Row(table, "Ngành nghề", book.IndustryName);
                });

                column.Item().Background(Colors.Grey.Lighten4).Padding(10).Column(box =>
                {
                    box.Item().Row(row =>
                    {
                        row.RelativeItem().Text("Tổng doanh thu năm").Bold();
                        row.AutoItem().Text($"{book.Total.ToString("N0", Vietnamese)} đ").FontSize(15).Bold();
                    });
                    box.Item().PaddingTop(4).Text(
                        $"Đơn hàng StreetBiz {book.Online.ToString("N0", Vietnamese)} đ + bán trực tiếp {book.Recorded.ToString("N0", Vietnamese)} đ")
                        .FontColor(Colors.Grey.Darken1);
                    box.Item().PaddingTop(6).Text(text =>
                    {
                        text.Span("Ngưỡng không chịu thuế: ").FontColor(Colors.Grey.Darken1);
                        text.Span(TaxFormat.Money(book.Threshold)).Bold();
                        text.Span("  ·  ");
                        text.Span($"{GroupLabel(book.Group)}: {book.GroupDescription}").Bold();
                    });
                    box.Item().Text(LevelLabel(book.Level)).Bold();
                });

                column.Item().Text("Doanh thu theo tháng").FontSize(11).Bold();
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(1.2f);
                        columns.RelativeColumn(2);
                        columns.RelativeColumn(2);
                        columns.RelativeColumn(2);
                    });
                    table.Header(header =>
                    {
                        foreach (var title in new[] { "Tháng", "Đơn hàng StreetBiz", "Bán trực tiếp", "Tổng" })
                        {
                            header.Cell().Background(Colors.Grey.Lighten3).Padding(4).Text(title).Bold();
                        }
                    });
                    foreach (var month in book.Months)
                    {
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text($"Tháng {month.Month}");
                        MoneyCell(table, month.Online);
                        MoneyCell(table, month.Recorded);
                        MoneyCell(table, month.Total);
                    }

                    table.Cell().Padding(4).Text("Cả năm").Bold();
                    MoneyCell(table, book.Online, bold: true);
                    MoneyCell(table, book.Recorded, bold: true);
                    MoneyCell(table, book.Total, bold: true);
                });

                column.Item().Text("Theo quý").FontSize(11).Bold();
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(1.2f);
                        columns.RelativeColumn(2);
                        columns.RelativeColumn(2);
                        columns.RelativeColumn(2);
                    });
                    foreach (var (quarter, online, recorded) in quarters)
                    {
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text($"Quý {quarter}");
                        MoneyCell(table, online);
                        MoneyCell(table, recorded);
                        MoneyCell(table, online + recorded);
                    }
                });

                if (book.Filings.Count > 0)
                {
                    column.Item().Text("Kỳ thông báo và khai thuế").FontSize(11).Bold();
                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(3);
                            columns.RelativeColumn(1.4f);
                            columns.RelativeColumn(2);
                            columns.RelativeColumn(2);
                        });
                        foreach (var filing in book.Filings)
                        {
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(filing.Label);
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3)
                                .Text($"Hạn {filing.DueDate:dd/MM/yyyy}");
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3)
                                .Text(PeriodStatusLabel(filing.Status));
                            table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(
                                filing.FiledOn is { } filed
                                    ? $"Nộp {filed:dd/MM/yyyy}{(string.IsNullOrWhiteSpace(filing.AuthorityReference) ? "" : " · " + filing.AuthorityReference)}"
                                    : "");
                        }
                    });
                }
            });

            page.Footer().Column(column =>
            {
                column.Item().Text(TaxMessages.Disclaimer).FontSize(7).FontColor(Colors.Grey.Darken1);
                column.Item().PaddingTop(2).Text($"In lúc {Local(printedAtUtc):HH:mm dd/MM/yyyy}").FontSize(7).FontColor(Colors.Grey.Medium);
            });
        })).GeneratePdf();
    }

    private static void Row(TableDescriptor table, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        table.Cell().PaddingVertical(2).Text(label).FontColor(Colors.Grey.Darken1);
        table.Cell().PaddingVertical(2).Text(value);
    }

    private static void MoneyCell(TableDescriptor table, decimal amount, bool bold = false)
    {
        var cell = table.Cell().BorderBottom(bold ? 0 : 0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).AlignRight()
            .Text(amount.ToString("N0", Vietnamese));
        if (bold)
        {
            cell.Bold();
        }
    }

    // ------------------------------------------------------------------------------- ward

    public byte[] RenderWardTaxXlsx(WardTaxExport export)
    {
        using var workbook = new XLWorkbook();
        WriteWardSummary(workbook.Worksheets.Add("Tổng hợp"), export);
        WriteWardHouseholds(workbook.Worksheets.Add("Hộ kinh doanh"), export.Households);
        WriteWardReferrals(workbook.Worksheets.Add("Đề nghị cơ quan thuế"), export.Referrals);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteWardSummary(IXLWorksheet sheet, WardTaxExport export)
    {
        sheet.Cell("A1").Value = $"DANH SÁCH HỘ KINH DOANH VÀ ĐỀ NGHỊ CƠ QUAN THUẾ NĂM {export.TaxYear}";
        sheet.Cell("A1").Style.Font.SetBold().Font.SetFontSize(14);
        sheet.Cell("A2").Value = $"UBND {export.WardName}";
        sheet.Cell("A3").Value = $"Lập lúc {Local(export.GeneratedAt):HH:mm dd/MM/yyyy}";

        Header(sheet.Row(5), "Chỉ tiêu", "Giá trị");
        var row = 6;
        foreach (var (label, value) in export.Summary)
        {
            sheet.Cell(row, 1).Value = label;
            sheet.Cell(row, 2).Value = value;
            sheet.Cell(row, 2).Style.Alignment.WrapText = true;
            row++;
        }

        sheet.Column(1).Width = 56;
        sheet.Column(2).Width = 60;
    }

    private static void WriteWardHouseholds(IXLWorksheet sheet, IReadOnlyList<WardTaxExportHousehold> households)
    {
        Header(sheet.Row(1), "STT", "Hộ kinh doanh", "Chủ hộ", "Điện thoại", "Địa chỉ", "Ngành nghề", "Tình trạng",
            "Ô đang thuê", "Ngày duyệt đăng ký", "Kiểm tra thực địa gần nhất", "Mã số thuế (che)",
            "Đã thông báo doanh thu năm", "Doanh thu năm (hộ đồng ý chia sẻ)", "Nhóm doanh thu");
        var row = 2;
        foreach (var household in households)
        {
            sheet.Cell(row, 1).Value = row - 1;
            sheet.Cell(row, 2).Value = household.DisplayName;
            sheet.Cell(row, 3).Value = household.OwnerName;
            sheet.Cell(row, 4).SetValue(household.Phone ?? "");
            sheet.Cell(row, 5).Value = household.Address ?? "";
            sheet.Cell(row, 6).Value = household.BusinessLine ?? "";
            sheet.Cell(row, 7).Value = HouseholdStatusLabel(household.Status);
            sheet.Cell(row, 8).Value = household.SlotCodes;
            sheet.Cell(row, 9).Value = Local(household.ApprovedAt);
            sheet.Cell(row, 9).Style.DateFormat.Format = "dd/MM/yyyy";
            if (household.LastInspectionAt is { } inspected)
            {
                sheet.Cell(row, 10).Value = Local(inspected);
                sheet.Cell(row, 10).Style.DateFormat.Format = "dd/MM/yyyy";
            }

            sheet.Cell(row, 11).Value = household.TaxCodeMasked;
            sheet.Cell(row, 12).Value = household.NoticeFiled ? "Đã thông báo" : "Chưa";
            if (household.Revenue is { } revenue)
            {
                Money(sheet.Cell(row, 13), revenue);
                sheet.Cell(row, 14).Value = household.Group is { } group ? GroupLabel(group) : "";
            }
            else
            {
                sheet.Cell(row, 13).Value = "Không chia sẻ";
                sheet.Cell(row, 13).Style.Font.SetFontColor(XLColor.Gray);
            }

            row++;
        }

        sheet.SheetView.FreezeRows(1);
        double[] widths = [6, 30, 22, 14, 38, 28, 16, 18, 16, 18, 18, 18, 24, 14];
        for (var column = 0; column < widths.Length; column++)
        {
            sheet.Column(column + 1).Width = widths[column];
        }
    }

    private static void WriteWardReferrals(IXLWorksheet sheet, IReadOnlyList<WardTaxExportReferral> referrals)
    {
        Header(sheet.Row(1), "STT", "Loại", "Hộ kinh doanh", "Nội dung", "Ngày phát hiện", "Tình trạng", "Ngày xử lý",
            "Số công văn / tham chiếu", "Ghi chú");
        var row = 2;
        foreach (var referral in referrals)
        {
            sheet.Cell(row, 1).Value = row - 1;
            sheet.Cell(row, 2).Value = referral.ReasonLabel;
            sheet.Cell(row, 3).Value = referral.VendorName ?? "Chưa xác định";
            sheet.Cell(row, 4).Value = referral.Summary;
            sheet.Cell(row, 4).Style.Alignment.WrapText = true;
            sheet.Cell(row, 5).Value = Local(referral.DetectedAt);
            sheet.Cell(row, 5).Style.DateFormat.Format = "dd/MM/yyyy";
            sheet.Cell(row, 6).Value = ReferralStatusLabel(referral.Status);
            if (referral.HandledAt is { } handled)
            {
                sheet.Cell(row, 7).Value = Local(handled);
                sheet.Cell(row, 7).Style.DateFormat.Format = "dd/MM/yyyy";
            }

            sheet.Cell(row, 8).Value = referral.AuthorityReference ?? "";
            sheet.Cell(row, 9).Value = referral.HandlingNote ?? "";
            row++;
        }

        sheet.SheetView.FreezeRows(1);
        double[] widths = [6, 34, 28, 70, 15, 20, 15, 26, 40];
        for (var column = 0; column < widths.Length; column++)
        {
            sheet.Column(column + 1).Width = widths[column];
        }
    }

    // ----------------------------------------------------------------------------- helpers

    private static string GroupLabel(string group) => group switch
    {
        TaxGroups.One => "Nhóm 1",
        TaxGroups.Two => "Nhóm 2",
        TaxGroups.Three => "Nhóm 3",
        _ => "Nhóm 4",
    };

    private static string LevelLabel(string level) => level switch
    {
        ThresholdLevels.Over => "Đã vượt ngưỡng không chịu thuế",
        ThresholdLevels.Near => "Gần ngưỡng không chịu thuế",
        _ => "Chưa vượt ngưỡng không chịu thuế",
    };

    private static string PeriodStatusLabel(string status) => status switch
    {
        TaxPeriodStatuses.Filed => "Đã nộp",
        TaxPeriodStatuses.Overdue => "Quá hạn",
        TaxPeriodStatuses.DueSoon => "Sắp đến hạn",
        TaxPeriodStatuses.Open => "Đến kỳ khai",
        TaxPeriodStatuses.NotYet => "Kỳ chưa kết thúc",
        _ => "Chưa phải khai",
    };

    private static string HouseholdStatusLabel(string status) => status switch
    {
        HouseholdStatuses.Active => "Đang hoạt động",
        HouseholdStatuses.Suspended => "Tạm đình chỉ",
        HouseholdStatuses.Ended => "Đã ngừng",
        _ => "Chưa thuê ô",
    };

    private static string ReferralStatusLabel(string status) => status switch
    {
        TaxReferralStatuses.Reported => "Đã báo cơ quan thuế",
        TaxReferralStatuses.Dismissed => "Không cần báo",
        _ => "Chờ xử lý",
    };

    private static void Header(IXLRow row, params string[] titles)
    {
        for (var index = 0; index < titles.Length; index++)
        {
            var cell = row.Cell(index + 1);
            cell.Value = titles[index];
            cell.Style.Font.SetBold()
                .Fill.SetBackgroundColor(XLColor.FromHtml("#EDEDED"))
                .Border.SetBottomBorder(XLBorderStyleValues.Thin);
        }
    }

    private static void Money(IXLCell cell, decimal amount)
    {
        cell.Value = amount;
        cell.Style.NumberFormat.Format = MoneyFormat;
    }

    private static DateTime Local(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), BusinessCalendar.TimeZone);
}
