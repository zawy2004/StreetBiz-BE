using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Infrastructure.Documents;

/// <summary>
/// ADM-07 files (ClosedXML for .xlsx, the same library the ward exports use; a plain writer for
/// .csv). Text that a spreadsheet could read as a formula is neutralised first: a report can carry
/// words typed by users (a report reason, a storefront name), and opening the file must never
/// run them.
/// </summary>
public sealed class PlatformReportRenderer : IPlatformReportRenderer
{
    private const int MaxSheetNameLength = 31;
    private const int SizedRows = 200;
    private static readonly char[] ForbiddenSheetNameChars = ['[', ']', ':', '*', '?', '/', '\\'];

    public byte[] RenderXlsx(PlatformReportDataset dataset)
    {
        using var workbook = new XLWorkbook();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in dataset.Sheets)
        {
            var worksheet = workbook.Worksheets.Add(UniqueSheetName(sheet.Name, names));
            for (var column = 0; column < sheet.Headers.Count; column++)
            {
                var header = worksheet.Cell(1, column + 1);
                header.Value = SafeText(sheet.Headers[column]);
                header.Style.Font.SetBold();
                header.Style.Fill.SetBackgroundColor(XLColor.LightGray);
            }

            for (var row = 0; row < sheet.Rows.Count; row++)
            {
                var cells = sheet.Rows[row];
                for (var column = 0; column < cells.Count; column++)
                {
                    WriteCell(worksheet.Cell(row + 2, column + 1), cells[column]);
                }
            }

            worksheet.SheetView.FreezeRows(1);
            // Sizing every row of a 50,000-row sheet is slow and gains nothing: the head is enough.
            worksheet.Columns().AdjustToContents(1, Math.Min(sheet.Rows.Count + 1, SizedRows));
        }

        if (workbook.Worksheets.Count == 0)
        {
            workbook.Worksheets.Add("Báo cáo");
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public byte[] RenderCsv(PlatformReportDataset dataset)
    {
        var text = new StringBuilder();
        var first = true;
        foreach (var sheet in dataset.Sheets)
        {
            if (!first)
            {
                text.Append("\r\n");
            }

            first = false;
            // One CSV cannot hold several tables: each becomes a titled section.
            if (dataset.Sheets.Count > 1)
            {
                AppendRow(text, [sheet.Name]);
            }

            AppendRow(text, sheet.Headers.Select(header => (object?)header).ToArray());
            foreach (var row in sheet.Rows)
            {
                AppendRow(text, row);
            }
        }

        var body = Encoding.UTF8.GetBytes(text.ToString());
        return [.. Encoding.UTF8.GetPreamble(), .. body];
    }

    /// <summary>Text starting with = + - @ (or a tab / carriage return) is prefixed with an apostrophe.</summary>
    public static string SafeText(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + value
            : value;

    private static void WriteCell(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case string text:
                cell.Value = SafeText(text);
                break;
            case decimal number:
                cell.Value = number;
                cell.Style.NumberFormat.Format = "#,##0.##";
                break;
            case double number:
                cell.Value = number;
                break;
            case int number:
                cell.Value = number;
                break;
            case long number:
                cell.Value = number;
                break;
            default:
                cell.Value = SafeText(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }
    }

    private static void AppendRow(StringBuilder text, IReadOnlyList<object?> cells)
    {
        for (var column = 0; column < cells.Count; column++)
        {
            if (column > 0)
            {
                text.Append(',');
            }

            text.Append(CsvField(cells[column]));
        }

        text.Append("\r\n");
    }

    private static string CsvField(object? value)
    {
        // Numbers keep a '.' decimal and no thousands separator: the file is for machines to read.
        var field = value switch
        {
            null => string.Empty,
            string text => SafeText(text),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => SafeText(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
        };
        return field.IndexOfAny([',', '"', '\r', '\n']) >= 0 || field.StartsWith(' ') || field.EndsWith(' ')
            ? "\"" + field.Replace("\"", "\"\"") + "\""
            : field;
    }

    private static string UniqueSheetName(string name, HashSet<string> used)
    {
        var clean = new string(name.Where(c => !ForbiddenSheetNameChars.Contains(c)).ToArray()).Trim();
        if (clean.Length == 0)
        {
            clean = "Báo cáo";
        }

        clean = clean[..Math.Min(clean.Length, MaxSheetNameLength)];
        var candidate = clean;
        for (var n = 2; !used.Add(candidate); n++)
        {
            var suffix = $" {n}";
            candidate = clean[..Math.Min(clean.Length, MaxSheetNameLength - suffix.Length)] + suffix;
        }

        return candidate;
    }
}
