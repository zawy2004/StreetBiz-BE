using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace StreetBiz.Infrastructure.Services.Documents;

/// <summary>
/// Shared .docx building blocks for every government-style document the system generates
/// (biên bản vi phạm hành chính, hồ sơ đăng ký hộ kinh doanh, ...). Centralising the quốc
/// hiệu/tiêu ngữ block here means every such document gets the identical, correctly worded
/// "CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM / Độc lập - Tự do - Hạnh phúc" header by construction,
/// instead of each feature re-typing (and risking drift in) the same three lines.
/// </summary>
public static class OfficialDocumentBuilder
{
    /// <summary>
    /// Two-column masthead: issuing body + optional document-number line on the left,
    /// quốc hiệu/tiêu ngữ on the right. `docNumberLabel` is null when the document has no
    /// number of its own (e.g. a registration filing, which is identified by its own record id
    /// elsewhere in the body rather than a "Số: .../..." line).
    /// </summary>
    public static Table NationalHeaderTable(string issuerName, string? docNumberLabel)
    {
        var table = BorderlessTable();
        var row = new TableRow();
        var leftCell = new TableCell(P(issuerName.ToUpperInvariant(), bold: true, align: JustificationValues.Center));
        if (docNumberLabel is not null)
        {
            leftCell.Append(P("________", align: JustificationValues.Center));
            leftCell.Append(P(docNumberLabel, align: JustificationValues.Center));
        }
        row.Append(
            leftCell,
            new TableCell(
                P("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM", bold: true, align: JustificationValues.Center),
                P("Độc lập - Tự do - Hạnh phúc", bold: true, align: JustificationValues.Center),
                P("_______________________", align: JustificationValues.Center)));
        table.Append(row);
        return table;
    }

    /// <summary>
    /// Header for a document a citizen submits TO an agency (e.g. Mẫu số 01 Phụ lục II,
    /// TT 68/2025/TT-BTC -- "GIẤY ĐỀ NGHỊ ĐĂNG KÝ HỘ KINH DOANH") -- unlike
    /// <see cref="NationalHeaderTable"/>, there is no issuing-agency box on the left (the citizen
    /// is not an agency); that slot instead carries the form number the original template prints
    /// there ("Mẫu số 1"), matching the real template's layout exactly.
    /// </summary>
    public static Table PetitionHeaderTable(string formNumberLabel)
    {
        var table = BorderlessTable();
        var row = new TableRow();
        row.Append(
            new TableCell(P(formNumberLabel)),
            new TableCell(
                P("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM", bold: true, align: JustificationValues.Center),
                P("Độc lập - Tự do - Hạnh phúc", bold: true, align: JustificationValues.Center),
                P("_______________________", align: JustificationValues.Center)));
        table.Append(row);
        return table;
    }

    /// <summary>Two-column signature block; each tuple is (role label, instruction line).</summary>
    public static Table SignatureTable((string Title, string Instruction) left, (string Title, string Instruction) right)
    {
        var table = BorderlessTable();
        var row = new TableRow();
        row.Append(
            new TableCell(
                P(left.Title, bold: true, align: JustificationValues.Center),
                P(left.Instruction, align: JustificationValues.Center)),
            new TableCell(
                P(right.Title, bold: true, align: JustificationValues.Center),
                P(right.Instruction, align: JustificationValues.Center)));
        table.Append(row);
        return table;
    }

    public static Table BorderlessTable() => new(new TableProperties(new TableBorders(
        new TopBorder { Val = BorderValues.None }, new BottomBorder { Val = BorderValues.None },
        new LeftBorder { Val = BorderValues.None }, new RightBorder { Val = BorderValues.None },
        new InsideHorizontalBorder { Val = BorderValues.None }, new InsideVerticalBorder { Val = BorderValues.None })));

    public static Paragraph P(string text, bool bold = false, bool italic = false,
        JustificationValues? align = null, int? size = null, string? colorHex = null)
    {
        var runProps = new RunProperties();
        if (bold) runProps.Append(new Bold());
        if (italic) runProps.Append(new Italic());
        if (size.HasValue) runProps.Append(new FontSize { Val = size.Value.ToString() }); // half-points (OpenXml convention)
        if (colorHex is not null) runProps.Append(new Color { Val = colorHex });

        var paraProps = new ParagraphProperties();
        if (align.HasValue) paraProps.Append(new Justification { Val = align.Value });

        var run = new Run(runProps, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new Paragraph(paraProps, run);
    }
}
