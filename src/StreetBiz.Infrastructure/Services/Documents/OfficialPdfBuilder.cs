using MigraDocCore.DocumentObjectModel;
using MigraDocCore.DocumentObjectModel.Tables;
using MigraDocCore.Rendering;

namespace StreetBiz.Infrastructure.Services.Documents;

/// <summary>
/// PDF counterpart to <see cref="OfficialDocumentBuilder"/> (.docx). Built independently with
/// MigraDocCore/PdfSharpCore (MIT-licensed, no server-side Word/LibreOffice dependency) rather
/// than converting the generated .docx, since a reliable docx-&gt;pdf converter isn't guaranteed
/// to be present on the deployment host. Both builders take the same field values from the same
/// caller so the two output formats never drift in content, only in rendering engine.
/// </summary>
public static class OfficialPdfBuilder
{
    private const string BodyFont = "Times New Roman";

    public static Document NewDocument()
    {
        var document = new Document();
        document.Info.Title = "StreetBiz";
        var style = document.Styles["Normal"]!;
        style.Font.Name = BodyFont;
        style.Font.Size = 13;

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = "2cm";
        section.PageSetup.BottomMargin = "2cm";
        section.PageSetup.LeftMargin = "3cm";
        section.PageSetup.RightMargin = "2cm";
        return document;
    }

    public static Section Section(Document document) => document.LastSection;

    /// <summary>Two-column masthead identical in wording to <see cref="OfficialDocumentBuilder.NationalHeaderTable"/>.</summary>
    public static void AddNationalHeader(Section section, string issuerName, string? docNumberLabel)
    {
        var table = section.AddTable();
        table.Borders.Visible = false;
        table.AddColumn("8cm");
        table.AddColumn("8cm");
        var row = table.AddRow();

        var left = row.Cells[0];
        AddCenteredParagraph(left, issuerName.ToUpperInvariant(), bold: true);
        if (docNumberLabel is not null)
        {
            AddCenteredParagraph(left, "________");
            AddCenteredParagraph(left, docNumberLabel);
        }

        var right = row.Cells[1];
        AddCenteredParagraph(right, "CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM", bold: true);
        AddCenteredParagraph(right, "Độc lập - Tự do - Hạnh phúc", bold: true);
        AddCenteredParagraph(right, "_______________________");

        section.AddParagraph(); // spacer
    }

    /// <summary>PDF counterpart of <see cref="OfficialDocumentBuilder.PetitionHeaderTable"/> --
    /// a citizen-submitted document has no issuing-agency box; that slot carries the form number
    /// the real template prints there ("Mẫu số 1").</summary>
    public static void AddPetitionHeader(Section section, string formNumberLabel)
    {
        var table = section.AddTable();
        table.Borders.Visible = false;
        table.AddColumn("4cm");
        table.AddColumn("12cm");
        var row = table.AddRow();

        row.Cells[0].AddParagraph(formNumberLabel);

        var right = row.Cells[1];
        AddCenteredParagraph(right, "CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM", bold: true);
        AddCenteredParagraph(right, "Độc lập - Tự do - Hạnh phúc", bold: true);
        AddCenteredParagraph(right, "_______________________");

        section.AddParagraph(); // spacer
    }

    public static void AddSignatureBlock(Section section, (string Title, string Instruction) left, (string Title, string Instruction) right)
    {
        var table = section.AddTable();
        table.Borders.Visible = false;
        table.AddColumn("8cm");
        table.AddColumn("8cm");
        var row = table.AddRow();
        AddCenteredParagraph(row.Cells[0], left.Title, bold: true);
        AddCenteredParagraph(row.Cells[0], left.Instruction);
        AddCenteredParagraph(row.Cells[1], right.Title, bold: true);
        AddCenteredParagraph(row.Cells[1], right.Instruction);
    }

    public static Paragraph AddTitle(Section section, string text)
    {
        var p = section.AddParagraph(text);
        p.Format.Font.Bold = true;
        p.Format.Font.Size = 15;
        p.Format.Alignment = ParagraphAlignment.Center;
        return p;
    }

    public static Paragraph AddSubtitle(Section section, string text)
    {
        var p = section.AddParagraph(text);
        p.Format.Font.Italic = true;
        p.Format.Alignment = ParagraphAlignment.Center;
        return p;
    }

    /// <summary>Bold, red, centered banner used while a filing has not yet been approved.</summary>
    public static void AddDraftWatermark(Section section)
    {
        var p = section.AddParagraph("BẢN NHÁP — CHƯA ĐƯỢC PHÊ DUYỆT");
        p.Format.Font.Bold = true;
        p.Format.Font.Size = 12;
        p.Format.Font.Color = Colors.Red;
        p.Format.Alignment = ParagraphAlignment.Center;
        p.Format.SpaceAfter = "0.4cm";
    }

    public static Paragraph AddRightAligned(Section section, string text)
    {
        var p = section.AddParagraph(text);
        p.Format.Alignment = ParagraphAlignment.Right;
        p.Format.SpaceAfter = "0.3cm";
        return p;
    }

    public static Paragraph AddBody(Section section, string text, bool bold = false)
    {
        var p = section.AddParagraph(text);
        p.Format.Font.Bold = bold;
        p.Format.SpaceAfter = "0.15cm";
        return p;
    }

    public static Paragraph AddSpacer(Section section) => section.AddParagraph();

    private static void AddCenteredParagraph(Cell cell, string text, bool bold = false)
    {
        var p = cell.AddParagraph(text);
        p.Format.Alignment = ParagraphAlignment.Center;
        p.Format.Font.Bold = bold;
    }

    public static byte[] Render(Document document)
    {
        // unicode:true is required -- without it PdfDocumentRenderer defaults to WinAnsi (cp1252)
        // encoding, which silently drops every Vietnamese diacritic (Độc lập renders as "c l p").
        var renderer = new PdfDocumentRenderer(unicode: true) { Document = document };
        renderer.RenderDocument();
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream);
        return stream.ToArray();
    }
}
