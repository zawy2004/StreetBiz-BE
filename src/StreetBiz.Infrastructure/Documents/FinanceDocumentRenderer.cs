using System.Globalization;
using ClosedXML.Excel;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.DTOs.Finance;

namespace StreetBiz.Infrastructure.Documents;

/// <summary>
/// FEE-03 receipt PDF (QuestPDF, Community licence) and WARD-14 workbook (ClosedXML). Every
/// timestamp is shown in Đà Nẵng time; the PDF uses the embedded Be Vietnam Pro font so the
/// diacritics never depend on what the server has installed.
/// </summary>
public sealed class FinanceDocumentRenderer : IFinanceDocumentRenderer
{
    private const string Font = "Be Vietnam Pro";
    private const string MoneyFormat = "#,##0";
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");
    private static readonly Lazy<bool> Setup = new(RegisterFonts, isThreadSafe: true);

    public byte[] RenderInvoicePdf(InvoiceDocumentRow invoice, DateTime printedAtUtc)
    {
        _ = Setup.Value;
        var isPenalty = invoice.Kind == "PENALTY";
        var content = isPenalty
            ? $"Tiền phạt vi phạm hành chính: {invoice.ViolationLabel}"
            : $"Phí sử dụng tạm thời vỉa hè — ô {invoice.SlotCode}, {invoice.PeriodLabel}";

        var rows = new List<(string Label, string? Value)>
        {
            ("Người nộp tiền", invoice.PayerName),
            ("Hộ kinh doanh", invoice.BusinessName),
            ("Số điện thoại", invoice.PayerPhone),
            ("Nội dung", content),
        };
        if (isPenalty)
        {
            rows.Add(("Quyết định xử phạt", invoice.DecisionNumber));
            rows.Add(("Ô vỉa hè", invoice.SlotCode));
        }

        rows.Add(("Hình thức thanh toán", Provider(invoice.PaymentProvider)));
        rows.Add(("Mã giao dịch", invoice.ProviderReference));
        rows.Add(("Thời điểm thanh toán", invoice.PaidAt is { } paidAt ? $"{Local(paidAt):HH:mm dd/MM/yyyy}" : null));

        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A5);
            page.Margin(28);
            page.DefaultTextStyle(style => style.FontFamily(Font).FontSize(9.5f).FontColor(Colors.Grey.Darken4));

            page.Header().Row(row =>
            {
                row.RelativeItem().Column(column =>
                {
                    column.Item().Text(invoice.WardName is null
                        ? "ỦY BAN NHÂN DÂN PHƯỜNG"
                        : $"UBND {invoice.WardName}".ToUpper(Vietnamese)).Bold();
                    column.Item().Text("Nền tảng quản lý vỉa hè StreetBiz").FontSize(8).FontColor(Colors.Grey.Darken1);
                });
                row.ConstantItem(150).Column(column =>
                {
                    column.Item().AlignRight().Text(text =>
                    {
                        text.Span("Số: ").FontColor(Colors.Grey.Darken1);
                        text.Span(invoice.InvoiceNumber).Bold();
                    });
                    column.Item().AlignRight().Text($"Ngày {Local(invoice.IssuedAt):dd/MM/yyyy}")
                        .FontColor(Colors.Grey.Darken1);
                });
            });

            page.Content().PaddingVertical(16).Column(column =>
            {
                column.Spacing(4);
                column.Item().AlignCenter().Text("BIÊN LAI THANH TOÁN").FontSize(15).Bold();
                column.Item().AlignCenter()
                    .Text(isPenalty ? "Tiền phạt vi phạm hành chính" : "Phí sử dụng tạm thời vỉa hè")
                    .FontColor(Colors.Grey.Darken1);

                column.Item().PaddingTop(14).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(118);
                        columns.RelativeColumn();
                    });
                    foreach (var (label, value) in rows.Where(row => !string.IsNullOrWhiteSpace(row.Value)))
                    {
                        table.Cell().PaddingVertical(3).Text(label).FontColor(Colors.Grey.Darken1);
                        table.Cell().PaddingVertical(3).Text(value!);
                    }
                });

                column.Item().PaddingTop(12).Background(Colors.Grey.Lighten4).Padding(10).Column(amount =>
                {
                    amount.Item().Row(row =>
                    {
                        row.RelativeItem().Text("Số tiền").Bold();
                        row.AutoItem().Text($"{invoice.Amount.ToString("N0", Vietnamese)} đ").FontSize(14).Bold();
                    });
                    amount.Item().PaddingTop(4).Text(text =>
                    {
                        text.Span("Bằng chữ: ").FontColor(Colors.Grey.Darken1);
                        text.Span($"{VietnameseMoneyWords.Of(invoice.Amount)}.");
                    });
                });
            });

            page.Footer().Column(column =>
            {
                column.Item().Text(
                        "Chứng từ do hệ thống StreetBiz tự động phát hành ngay sau khi cổng thanh toán xác nhận " +
                        "giao dịch. Đây là chứng từ ghi nhận thanh toán nội bộ, không thay thế biên lai thu phí, " +
                        "lệ phí hoặc biên lai thu tiền phạt do cơ quan có thẩm quyền phát hành.")
                    .FontSize(7).FontColor(Colors.Grey.Darken1);
                column.Item().PaddingTop(2).Text($"In lúc {Local(printedAtUtc):HH:mm dd/MM/yyyy}")
                    .FontSize(7).FontColor(Colors.Grey.Medium);
            });
        })).GeneratePdf();
    }

    public byte[] RenderCollectionReportXlsx(CollectionReportExport export)
    {
        using var workbook = new XLWorkbook();
        WriteSummary(workbook.Worksheets.Add("Tổng hợp"), export);
        WriteReceipts(workbook.Worksheets.Add("Bảng kê thu"), export.Invoices);
        WriteDebtors(workbook.Worksheets.Add("Nợ phí"), export.Debtors);
        if (export.Performance is { } performance)
        {
            WriteZones(workbook.Worksheets.Add("Theo khu vực"), performance.ByZone);
        }

        WriteViolations(workbook.Worksheets.Add("Vi phạm gần đây"), export.Report.RecentViolations);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public byte[] RenderVendorStatementXlsx(VendorStatementExport statement)
    {
        using var workbook = new XLWorkbook();
        WriteStatementSummary(workbook.Worksheets.Add("Tổng hợp"), statement);
        WriteStatementReceipts(workbook.Worksheets.Add("Chứng từ"), statement.Invoices);
        WriteStatementInstalments(workbook.Worksheets.Add("Lịch phí"), statement.Instalments);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteStatementSummary(IXLWorksheet sheet, VendorStatementExport statement)
    {
        sheet.Cell("A1").Value = $"SAO KÊ PHÍ THUÊ VỈA HÈ VÀ TIỀN PHẠT NĂM {statement.Year}";
        sheet.Cell("A1").Style.Font.SetBold().Font.SetFontSize(14);
        sheet.Cell("A2").Value = statement.BusinessName is { Length: > 0 } business
            ? $"{statement.VendorName} · {business}"
            : statement.VendorName;
        sheet.Cell("A3").Value = $"Lập lúc {Local(statement.GeneratedAt):HH:mm dd/MM/yyyy}";

        var paid = statement.Instalments.Where(item => item.Status == "PAID").ToList();
        var owed = statement.Instalments.Where(item => item.Status != "PAID").ToList();
        Header(sheet.Row(5), "Chỉ tiêu", "Giá trị");
        (string Label, decimal Amount, bool IsMoney)[] lines =
        [
            ("Phí thuê ô đã nộp (theo chứng từ trong năm)", statement.Invoices.Where(i => i.Kind != "PENALTY").Sum(i => i.Amount), true),
            ("Tiền phạt đã nộp (theo chứng từ trong năm)", statement.Invoices.Where(i => i.Kind == "PENALTY").Sum(i => i.Amount), true),
            ("Số kỳ phí đến hạn trong năm", statement.Instalments.Count, false),
            ("Số kỳ đã thanh toán", paid.Count, false),
            ("Số tiền các kỳ chưa thanh toán", owed.Sum(item => item.Amount), true),
        ];
        var row = 6;
        foreach (var (label, amount, isMoney) in lines)
        {
            sheet.Cell(row, 1).Value = label;
            if (isMoney)
            {
                Money(sheet.Cell(row, 2), amount);
            }
            else
            {
                sheet.Cell(row, 2).Value = amount;
            }

            row++;
        }

        sheet.Cell(row + 1, 1).Value =
            "Chứng từ do StreetBiz tự động lập sau khi cổng thanh toán xác nhận; không thay thế biên lai thu phí, lệ phí của cơ quan có thẩm quyền.";
        sheet.Cell(row + 1, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);
        sheet.Column(1).Width = 50;
        sheet.Column(2).Width = 18;
    }

    private static void WriteStatementReceipts(IXLWorksheet sheet, IReadOnlyList<InvoiceRow> invoices)
    {
        Header(sheet.Row(1), "STT", "Số chứng từ", "Ngày phát hành", "Loại", "Ô", "Nội dung", "Hình thức", "Số tiền (đ)");
        var row = 2;
        foreach (var invoice in invoices.OrderBy(invoice => invoice.IssuedAt))
        {
            sheet.Cell(row, 1).Value = row - 1;
            sheet.Cell(row, 2).Value = invoice.InvoiceNumber;
            sheet.Cell(row, 3).Value = Local(invoice.IssuedAt);
            sheet.Cell(row, 3).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
            sheet.Cell(row, 4).Value = invoice.Kind == "PENALTY" ? "Tiền phạt" : "Phí thuê ô";
            sheet.Cell(row, 5).Value = invoice.SlotCode ?? "";
            sheet.Cell(row, 6).Value = invoice.Kind == "PENALTY"
                ? invoice.ViolationLabel ?? ""
                : invoice.PeriodLabel ?? "";
            sheet.Cell(row, 7).Value = Provider(invoice.PaymentProvider) ?? "";
            Money(sheet.Cell(row, 8), invoice.Amount);
            row++;
        }

        sheet.Cell(row, 7).Value = "Tổng cộng";
        sheet.Cell(row, 8).FormulaA1 = invoices.Count == 0 ? "0" : $"SUM(H2:H{row - 1})";
        sheet.Cell(row, 8).Style.NumberFormat.Format = MoneyFormat;
        sheet.Range(row, 7, row, 8).Style.Font.SetBold();
        sheet.SheetView.FreezeRows(1);
        double[] widths = [6, 18, 17, 12, 9, 44, 12, 16];
        for (var column = 0; column < widths.Length; column++)
        {
            sheet.Column(column + 1).Width = widths[column];
        }
    }

    private static void WriteStatementInstalments(IXLWorksheet sheet, IReadOnlyList<StatementInstalmentRow> instalments)
    {
        Header(sheet.Row(1), "Ô", "Kỳ phí", "Hạn nộp", "Số tiền (đ)", "Tình trạng", "Ngày nộp", "Số chứng từ");
        var row = 2;
        foreach (var item in instalments.OrderBy(item => item.DueDate))
        {
            sheet.Cell(row, 1).Value = item.SlotCode;
            sheet.Cell(row, 2).Value = item.PeriodLabel;
            sheet.Cell(row, 3).Value = item.DueDate.ToDateTime(TimeOnly.MinValue);
            sheet.Cell(row, 3).Style.DateFormat.Format = "dd/MM/yyyy";
            Money(sheet.Cell(row, 4), item.Amount);
            sheet.Cell(row, 5).Value = item.Status switch
            {
                "PAID" => "Đã nộp",
                "OVERDUE" => "Quá hạn",
                _ => "Chưa đến hạn",
            };
            if (item.PaidAt is { } paidAt)
            {
                sheet.Cell(row, 6).Value = Local(paidAt);
                sheet.Cell(row, 6).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
            }

            sheet.Cell(row, 7).Value = item.InvoiceNumber ?? "";
            row++;
        }

        sheet.SheetView.FreezeRows(1);
        double[] widths = [9, 28, 12, 16, 14, 17, 18];
        for (var column = 0; column < widths.Length; column++)
        {
            sheet.Column(column + 1).Width = widths[column];
        }
    }

    private static void WriteSummary(IXLWorksheet sheet, CollectionReportExport export)
    {
        var report = export.Report;
        sheet.Cell("A1").Value = "BÁO CÁO THU PHÍ THUÊ VỈA HÈ VÀ TIỀN PHẠT";
        sheet.Cell("A1").Style.Font.SetBold().Font.SetFontSize(14);
        sheet.Cell("A2").Value = export.WardName;
        sheet.Cell("A3").Value = $"Kỳ báo cáo: {export.From:dd/MM/yyyy} – {export.To:dd/MM/yyyy}";
        sheet.Cell("A4").Value = $"Lập lúc {Local(export.GeneratedAt):HH:mm dd/MM/yyyy}";

        Header(sheet.Row(6), "Chỉ tiêu", "Số tiền (đ)");
        (string Label, decimal Amount)[] lines =
        [
            ("Phí thuê ô đã thu trong kỳ", report.FeeCollected),
            ("Tiền phạt đã thu trong kỳ", report.PenaltyCollected),
        ];
        var row = 7;
        foreach (var (label, amount) in lines)
        {
            sheet.Cell(row, 1).Value = label;
            Money(sheet.Cell(row, 2), amount);
            row++;
        }

        sheet.Cell(row, 1).Value = "Tổng đã thu trong kỳ";
        sheet.Cell(row, 2).FormulaA1 = "SUM(B7:B8)";
        sheet.Cell(row, 2).Style.NumberFormat.Format = MoneyFormat;
        sheet.Range(row, 1, row, 2).Style.Font.SetBold();
        row += 2;

        // Debt is a snapshot as of the export, not tied to the period (as on the screen).
        sheet.Cell(row++, 1).Value = "Số còn phải thu tại thời điểm lập báo cáo";
        (string Label, decimal Amount)[] owed =
        [
            ("Phí chưa đến hạn", report.FeePending),
            ("Phí quá hạn", report.FeeOverdue),
            ("Tiền phạt chưa nộp", report.PenaltyPending),
        ];
        foreach (var (label, amount) in owed)
        {
            sheet.Cell(row, 1).Value = label;
            Money(sheet.Cell(row, 2), amount);
            row++;
        }

        row++;
        sheet.Cell(row, 1).Value = "Số chứng từ phát hành trong kỳ";
        sheet.Cell(row, 2).Value = report.InvoiceCount;

        if (export.Performance is { } performance)
        {
            row += 2;
            sheet.Cell(row++, 1).Value = "Kỳ phí đến hạn trong kỳ báo cáo";
            sheet.Cell(row, 1).Value = "Số kỳ đến hạn";
            sheet.Cell(row++, 2).Value = performance.DueCount;
            sheet.Cell(row, 1).Value = "Nộp đúng hạn";
            sheet.Cell(row++, 2).Value = performance.PaidOnTimeCount;
            sheet.Cell(row, 1).Value = "Nộp trễ hạn";
            sheet.Cell(row++, 2).Value = performance.PaidLateCount;
            sheet.Cell(row, 1).Value = "Chưa nộp";
            sheet.Cell(row++, 2).Value = performance.UnpaidCount;
            sheet.Cell(row, 1).Value = "Tỷ lệ nộp đúng hạn";
            if (performance.OnTimeRate is { } rate)
            {
                sheet.Cell(row, 2).Value = rate;
                sheet.Cell(row, 2).Style.NumberFormat.Format = "0.0%";
            }
            else
            {
                sheet.Cell(row, 2).Value = "Không có kỳ đến hạn";
            }
        }

        sheet.Column(1).Width = 44;
        sheet.Column(2).Width = 18;
    }

    private static void WriteDebtors(IXLWorksheet sheet, IReadOnlyList<WardDebtorDto> debtors)
    {
        Header(sheet.Row(1), "STT", "Hộ kinh doanh", "Người đại diện", "Điện thoại", "Ô", "Khu vực",
            "Số kỳ quá hạn", "Nợ quá hạn (đ)", "Sắp đến hạn (đ)", "Hạn cũ nhất", "Số ngày quá hạn", "Nhắc gần nhất");
        var row = 2;
        foreach (var debtor in debtors)
        {
            sheet.Cell(row, 1).Value = row - 1;
            sheet.Cell(row, 2).Value = debtor.BusinessName ?? "";
            sheet.Cell(row, 3).Value = debtor.VendorName;
            sheet.Cell(row, 4).Value = debtor.VendorPhone ?? "";
            sheet.Cell(row, 5).Value = debtor.SlotCode;
            sheet.Cell(row, 6).Value = debtor.ZoneName;
            sheet.Cell(row, 7).Value = debtor.OverdueCount;
            Money(sheet.Cell(row, 8), debtor.OverdueAmount);
            Money(sheet.Cell(row, 9), debtor.UpcomingAmount);
            sheet.Cell(row, 10).Value = debtor.OldestDueDate.ToDateTime(TimeOnly.MinValue);
            sheet.Cell(row, 10).Style.DateFormat.Format = "dd/MM/yyyy";
            sheet.Cell(row, 11).Value = debtor.DaysOverdue;
            if (debtor.LastRemindedAt is { } reminded)
            {
                sheet.Cell(row, 12).Value = Local(reminded);
                sheet.Cell(row, 12).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
            }

            row++;
        }

        sheet.Cell(row, 7).Value = "Tổng nợ quá hạn";
        sheet.Cell(row, 8).FormulaA1 = debtors.Count == 0 ? "0" : $"SUM(H2:H{row - 1})";
        sheet.Cell(row, 8).Style.NumberFormat.Format = MoneyFormat;
        sheet.Range(row, 7, row, 8).Style.Font.SetBold();
        sheet.SheetView.FreezeRows(1);
        double[] widths = [6, 26, 22, 13, 9, 18, 13, 16, 16, 12, 14, 17];
        for (var column = 0; column < widths.Length; column++)
        {
            sheet.Column(column + 1).Width = widths[column];
        }
    }

    private static void WriteZones(IXLWorksheet sheet, IReadOnlyList<ZoneCollectionDto> zones)
    {
        Header(sheet.Row(1), "Khu vực", "Tổng số ô", "Ô đang cho thuê", "Phí đã thu trong kỳ (đ)", "Còn phải thu (đ)");
        var row = 2;
        foreach (var zone in zones)
        {
            sheet.Cell(row, 1).Value = zone.ZoneName;
            sheet.Cell(row, 2).Value = zone.SlotCount;
            sheet.Cell(row, 3).Value = zone.RentedSlots;
            Money(sheet.Cell(row, 4), zone.FeeCollected);
            Money(sheet.Cell(row, 5), zone.Outstanding);
            row++;
        }

        sheet.SheetView.FreezeRows(1);
        double[] widths = [26, 11, 15, 22, 18];
        for (var column = 0; column < widths.Length; column++)
        {
            sheet.Column(column + 1).Width = widths[column];
        }
    }

    private static void WriteReceipts(IXLWorksheet sheet, IReadOnlyList<WardInvoiceRow> invoices)
    {
        Header(sheet.Row(1), "STT", "Số chứng từ", "Ngày thu", "Loại", "Người nộp", "Ô", "Nội dung", "Hình thức",
            "Số tiền (đ)");
        var row = 2;
        foreach (var invoice in invoices)
        {
            sheet.Cell(row, 1).Value = row - 1;
            sheet.Cell(row, 2).Value = invoice.InvoiceNumber;
            sheet.Cell(row, 3).Value = Local(invoice.IssuedAt);
            sheet.Cell(row, 3).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
            sheet.Cell(row, 4).Value = invoice.Kind == "PENALTY" ? "Tiền phạt" : "Phí thuê ô";
            sheet.Cell(row, 5).Value = invoice.PayerName ?? "";
            sheet.Cell(row, 6).Value = invoice.SlotCode ?? "";
            sheet.Cell(row, 7).Value = invoice.Description;
            sheet.Cell(row, 8).Value = Provider(invoice.PaymentProvider) ?? "";
            Money(sheet.Cell(row, 9), invoice.Amount);
            row++;
        }

        sheet.Cell(row, 8).Value = "Tổng cộng";
        sheet.Cell(row, 9).FormulaA1 = invoices.Count == 0 ? "0" : $"SUM(I2:I{row - 1})";
        sheet.Cell(row, 9).Style.NumberFormat.Format = MoneyFormat;
        sheet.Range(row, 8, row, 9).Style.Font.SetBold();

        sheet.SheetView.FreezeRows(1);
        double[] widths = [6, 18, 17, 12, 24, 9, 52, 12, 16];
        for (var column = 0; column < widths.Length; column++)
        {
            sheet.Column(column + 1).Width = widths[column];
        }
    }

    private static void WriteViolations(IXLWorksheet sheet, IReadOnlyList<RecentViolationRow> violations)
    {
        Header(sheet.Row(1), "Ngày ghi nhận", "Loại vi phạm", "Hộ kinh doanh", "Ô", "Tiền phạt (đ)", "Tình trạng");
        var row = 2;
        foreach (var violation in violations)
        {
            sheet.Cell(row, 1).Value = Local(violation.RecordedAt);
            sheet.Cell(row, 1).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
            sheet.Cell(row, 2).Value = violation.ViolationLabel;
            sheet.Cell(row, 3).Value = violation.VendorName ?? "Chưa xác định";
            sheet.Cell(row, 4).Value = violation.SlotCode ?? "";
            if (violation.PenaltyAmount is { } amount)
            {
                Money(sheet.Cell(row, 5), amount);
            }

            sheet.Cell(row, 6).Value = violation.PenaltyStatus switch
            {
                "UNPAID" => "Chưa nộp phạt",
                "PAID" => "Đã nộp phạt",
                "WAIVED" => "Đã miễn phạt",
                "CANCELLED" => "Đã huỷ",
                _ => "Chưa ra quyết định xử phạt",
            };
            row++;
        }

        sheet.SheetView.FreezeRows(1);
        double[] widths = [17, 44, 24, 9, 16, 26];
        for (var column = 0; column < widths.Length; column++)
        {
            sheet.Column(column + 1).Width = widths[column];
        }
    }

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

    private static string? Provider(string? provider) => provider switch
    {
        "MOMO" => "MoMo",
        "ZALOPAY" => "ZaloPay",
        _ => provider,
    };

    private static DateTime Local(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), BusinessCalendar.TimeZone);

    private static bool RegisterFonts()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var assembly = typeof(FinanceDocumentRenderer).Assembly;
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(name => name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            FontManager.RegisterFontFromStream(stream);
        }

        return true;
    }
}
