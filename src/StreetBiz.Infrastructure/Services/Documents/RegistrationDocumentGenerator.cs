using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using StreetBiz.Application.Common.Models;
using static StreetBiz.Infrastructure.Services.Documents.OfficialDocumentBuilder;

namespace StreetBiz.Infrastructure.Services.Documents;

/// <summary>
/// Builds Mẫu số 01 (Phụ lục II, Thông tư 68/2025/TT-BTC -- "GIẤY ĐỀ NGHỊ ĐĂNG KÝ HỘ KINH
/// DOANH") as either .docx or .pdf, composed from scratch (not by editing the government-supplied
/// binary -- see the note on WardComplianceService.BuildBienBanDocx for why) so the section order
/// and field labels below mirror that template's numbered items 1-7 verbatim, minus its numbered
/// footnotes (usage instructions for someone filling the paper form by hand, not relevant to an
/// already-filled record) and minus items this project doesn't collect (registration-tax address,
/// VAT method, website/fax) since REG only tracks what Mẫu số 01 needs for a sidewalk vendor.
/// </summary>
public sealed class RegistrationDocumentGenerator : IRegistrationDocumentGenerator
{
    public Task<(byte[] Content, string FileName)> GenerateAsync(
        RegistrationDocumentData data, string format, CancellationToken ct)
    {
        var fileBase = $"DangKyHKD_{data.RegistrationId}";
        return format == "pdf"
            ? Task.FromResult<(byte[], string)>((BuildPdf(data), $"{fileBase}.pdf"))
            : Task.FromResult<(byte[], string)>((BuildDocx(data), $"{fileBase}.docx"));
    }

    private static bool IsApproved(RegistrationDocumentData d) => d.RegistrationStatus == "APPROVED";

    /// <summary>The template's "……, ngày … tháng … năm ……" line -- the date this printed copy
    /// was produced, in Vietnam local time. The place is the pilot city (AGENTS.md: Da Nang
    /// pilot); the template's own place field is the province/city, not the ward, which is
    /// already named separately in "Kính gửi:".</summary>
    private static string PlaceAndDateLine()
    {
        var nowVn = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
            TimeZoneInfo.CreateCustomTimeZone("Asia/Ho_Chi_Minh", TimeSpan.FromHours(7), "Asia/Ho_Chi_Minh", "Asia/Ho_Chi_Minh"));
        return $"Đà Nẵng, ngày {nowVn.Day} tháng {nowVn.Month} năm {nowVn.Year}";
    }

    private static string GenderLabel(string? gender) => gender switch
    {
        "MALE" => "Nam",
        "FEMALE" => "Nữ",
        "OTHER" => "Khác",
        _ => "……",
    };

    private static byte[] BuildDocx(RegistrationDocumentData d)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = mainPart.Document.AppendChild(new Body());

            body.AppendChild(PetitionHeaderTable("Mẫu số 1"));
            body.AppendChild(P(PlaceAndDateLine(), align: JustificationValues.Right));

            if (!IsApproved(d))
            {
                body.AppendChild(P("BẢN NHÁP — CHƯA ĐƯỢC PHÊ DUYỆT", bold: true, align: JustificationValues.Center, colorHex: "C00000"));
            }

            body.AppendChild(P("GIẤY ĐỀ NGHỊ ĐĂNG KÝ HỘ KINH DOANH", bold: true, align: JustificationValues.Center, size: 28));
            body.AppendChild(P($"Kính gửi: {d.WardName}"));
            body.AppendChild(P(""));

            body.AppendChild(P($"Tôi là: {d.OwnerFullName.ToUpperInvariant()}"));
            var dobLine = d.OwnerDateOfBirth is { } dob ? $"Sinh ngày: {dob:dd/MM/yyyy}" : "Sinh ngày: ……………";
            body.AppendChild(P($"{dobLine}    Giới tính: {GenderLabel(d.OwnerGender)}"));
            if (!string.IsNullOrWhiteSpace(d.IdNumber))
            {
                var idTypeLabel = d.IdType == "PASSPORT" ? "Số hộ chiếu" : "Số định danh cá nhân";
                var issued = d.IdIssuedDate is { } iid ? $"; ngày cấp: {iid:dd/MM/yyyy}" : "";
                var place = !string.IsNullOrWhiteSpace(d.IdIssuedPlace) ? $"; nơi cấp: {d.IdIssuedPlace}" : "";
                body.AppendChild(P($"{idTypeLabel}: {d.IdNumber}{issued}{place}"));
            }
            if (!string.IsNullOrWhiteSpace(d.OwnerEthnicity) || !string.IsNullOrWhiteSpace(d.OwnerNationality))
            {
                body.AppendChild(P($"Dân tộc: {d.OwnerEthnicity ?? "……"}    Quốc tịch: {d.OwnerNationality ?? "……"}"));
            }
            if (!string.IsNullOrWhiteSpace(d.PermanentAddress))
            {
                body.AppendChild(P($"Nơi thường trú: {d.PermanentAddress}"));
            }
            if (!string.IsNullOrWhiteSpace(d.ContactAddress))
            {
                body.AppendChild(P($"Nơi ở hiện tại: {d.ContactAddress}"));
            }

            body.AppendChild(P(""));
            body.AppendChild(P("Đăng ký hộ kinh doanh do tôi là chủ hộ với các nội dung sau:", bold: true));

            body.AppendChild(P("1. Tên hộ kinh doanh:", bold: true));
            body.AppendChild(P(d.DisplayName));

            body.AppendChild(P("2. Trụ sở của hộ kinh doanh:", bold: true));
            body.AppendChild(P(d.DeclaredAddress ?? "……………………………………………"));

            body.AppendChild(P("3. Ngành, nghề kinh doanh:", bold: true));
            body.AppendChild(P(!string.IsNullOrWhiteSpace(d.BusinessLine)
                ? $"{d.BusinessLine}{(string.IsNullOrWhiteSpace(d.BusinessLineCode) ? "" : $" (Mã ngành: {d.BusinessLineCode})")}"
                : "……………………………………………"));

            body.AppendChild(P("4. Vốn kinh doanh:", bold: true));
            body.AppendChild(P(d.CapitalAmount is { } cap ? $"{cap:N0} VNĐ" : "……………………………………………"));

            body.AppendChild(P("5. Ngày bắt đầu hoạt động:", bold: true));
            body.AppendChild(P(d.PlannedStartDate is { } start ? $"{start:dd/MM/yyyy}" : "……………………………………………"));
            body.AppendChild(P($"Tổng số lao động (dự kiến): {(d.LaborCount is { } lc ? lc.ToString() : "……")}"));

            if (d.HouseholdMembers.Count > 0)
            {
                body.AppendChild(P("6. Thành viên hộ gia đình cùng góp vốn:", bold: true));
                body.AppendChild(HouseholdMembersTable(d.HouseholdMembers));
            }

            if (d.FoodSafetyCommitmentAt is { } commitAt)
            {
                body.AppendChild(P(""));
                body.AppendChild(P($"Đã cam kết bảo đảm điều kiện an toàn thực phẩm lúc {commitAt:dd/MM/yyyy HH:mm}."));
            }

            body.AppendChild(P(""));
            body.AppendChild(P(
                "Tôi xin cam kết: bản thân không thuộc diện pháp luật cấm kinh doanh; trụ sở thuộc quyền sử dụng " +
                "hợp pháp của hộ kinh doanh; hoàn toàn chịu trách nhiệm trước pháp luật về tính hợp pháp, chính xác " +
                "và trung thực của nội dung đăng ký trên."));

            if (IsApproved(d) && d.ReviewedAt is { } reviewedAt)
            {
                body.AppendChild(P(""));
                var byWhom = string.IsNullOrWhiteSpace(d.ReviewedByName) ? "" : $", cán bộ duyệt: {d.ReviewedByName}";
                body.AppendChild(P($"Đã được {d.WardName} phê duyệt ngày {reviewedAt:dd/MM/yyyy HH:mm}{byWhom}.", bold: true));
            }

            body.AppendChild(P(""));
            body.AppendChild(SignatureTable(
                ("CHỦ HỘ KINH DOANH", "(Ký và ghi rõ họ tên)"),
                ("", "")));

            mainPart.Document.Save();
        }
        return stream.ToArray();
    }

    private static Table HouseholdMembersTable(IReadOnlyList<RegistrationDocumentHouseholdMember> members)
    {
        var table = new Table(new TableProperties(new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 4 }, new BottomBorder { Val = BorderValues.Single, Size = 4 },
            new LeftBorder { Val = BorderValues.Single, Size = 4 }, new RightBorder { Val = BorderValues.Single, Size = 4 },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));

        var header = new TableRow();
        header.Append(
            new TableCell(P("Họ tên", bold: true)),
            new TableCell(P("Ngày sinh", bold: true)),
            new TableCell(P("Số định danh", bold: true)),
            new TableCell(P("Quan hệ với chủ hộ", bold: true)),
            new TableCell(P("Vốn góp (VNĐ)", bold: true)));
        table.Append(header);

        foreach (var m in members)
        {
            var row = new TableRow();
            row.Append(
                new TableCell(P(m.FullName)),
                new TableCell(P(m.DateOfBirth is { } d ? $"{d:dd/MM/yyyy}" : "")),
                new TableCell(P(m.IdNumber ?? "")),
                new TableCell(P(m.RelationshipToOwner ?? "")),
                new TableCell(P(m.CapitalContribution is { } c ? $"{c:N0}" : "")));
            table.Append(row);
        }
        return table;
    }

    private static byte[] BuildPdf(RegistrationDocumentData d)
    {
        var document = OfficialPdfBuilder.NewDocument();
        var section = OfficialPdfBuilder.Section(document);

        OfficialPdfBuilder.AddPetitionHeader(section, "Mẫu số 1");
        OfficialPdfBuilder.AddRightAligned(section, PlaceAndDateLine());
        if (!IsApproved(d))
        {
            OfficialPdfBuilder.AddDraftWatermark(section);
        }
        OfficialPdfBuilder.AddTitle(section, "GIẤY ĐỀ NGHỊ ĐĂNG KÝ HỘ KINH DOANH");
        OfficialPdfBuilder.AddBody(section, $"Kính gửi: {d.WardName}");
        OfficialPdfBuilder.AddSpacer(section);

        OfficialPdfBuilder.AddBody(section, $"Tôi là: {d.OwnerFullName.ToUpperInvariant()}");
        var dobLine = d.OwnerDateOfBirth is { } dob ? $"Sinh ngày: {dob:dd/MM/yyyy}" : "Sinh ngày: ……………";
        OfficialPdfBuilder.AddBody(section, $"{dobLine}    Giới tính: {GenderLabel(d.OwnerGender)}");
        if (!string.IsNullOrWhiteSpace(d.IdNumber))
        {
            var idTypeLabel = d.IdType == "PASSPORT" ? "Số hộ chiếu" : "Số định danh cá nhân";
            var issued = d.IdIssuedDate is { } iid ? $"; ngày cấp: {iid:dd/MM/yyyy}" : "";
            var place = !string.IsNullOrWhiteSpace(d.IdIssuedPlace) ? $"; nơi cấp: {d.IdIssuedPlace}" : "";
            OfficialPdfBuilder.AddBody(section, $"{idTypeLabel}: {d.IdNumber}{issued}{place}");
        }
        if (!string.IsNullOrWhiteSpace(d.OwnerEthnicity) || !string.IsNullOrWhiteSpace(d.OwnerNationality))
        {
            OfficialPdfBuilder.AddBody(section, $"Dân tộc: {d.OwnerEthnicity ?? "……"}    Quốc tịch: {d.OwnerNationality ?? "……"}");
        }
        if (!string.IsNullOrWhiteSpace(d.PermanentAddress))
        {
            OfficialPdfBuilder.AddBody(section, $"Nơi thường trú: {d.PermanentAddress}");
        }
        if (!string.IsNullOrWhiteSpace(d.ContactAddress))
        {
            OfficialPdfBuilder.AddBody(section, $"Nơi ở hiện tại: {d.ContactAddress}");
        }

        OfficialPdfBuilder.AddSpacer(section);
        OfficialPdfBuilder.AddBody(section, "Đăng ký hộ kinh doanh do tôi là chủ hộ với các nội dung sau:", bold: true);

        OfficialPdfBuilder.AddBody(section, "1. Tên hộ kinh doanh:", bold: true);
        OfficialPdfBuilder.AddBody(section, d.DisplayName);

        OfficialPdfBuilder.AddBody(section, "2. Trụ sở của hộ kinh doanh:", bold: true);
        OfficialPdfBuilder.AddBody(section, d.DeclaredAddress ?? "……………………………………………");

        OfficialPdfBuilder.AddBody(section, "3. Ngành, nghề kinh doanh:", bold: true);
        OfficialPdfBuilder.AddBody(section, !string.IsNullOrWhiteSpace(d.BusinessLine)
            ? $"{d.BusinessLine}{(string.IsNullOrWhiteSpace(d.BusinessLineCode) ? "" : $" (Mã ngành: {d.BusinessLineCode})")}"
            : "……………………………………………");

        OfficialPdfBuilder.AddBody(section, "4. Vốn kinh doanh:", bold: true);
        OfficialPdfBuilder.AddBody(section, d.CapitalAmount is { } cap ? $"{cap:N0} VNĐ" : "……………………………………………");

        OfficialPdfBuilder.AddBody(section, "5. Ngày bắt đầu hoạt động:", bold: true);
        OfficialPdfBuilder.AddBody(section, d.PlannedStartDate is { } start ? $"{start:dd/MM/yyyy}" : "……………………………………………");
        OfficialPdfBuilder.AddBody(section, $"Tổng số lao động (dự kiến): {(d.LaborCount is { } lc ? lc.ToString() : "……")}");

        if (d.HouseholdMembers.Count > 0)
        {
            OfficialPdfBuilder.AddBody(section, "6. Thành viên hộ gia đình cùng góp vốn:", bold: true);
            var table = section.AddTable();
            table.Borders.Width = 0.5;
            foreach (var w in new[] { "3.2cm", "2.2cm", "2.8cm", "2.8cm", "2.8cm" }) table.AddColumn(w);
            var header = table.AddRow();
            header.Format.Font.Bold = true;
            header.Cells[0].AddParagraph("Họ tên");
            header.Cells[1].AddParagraph("Ngày sinh");
            header.Cells[2].AddParagraph("Số định danh");
            header.Cells[3].AddParagraph("Quan hệ với chủ hộ");
            header.Cells[4].AddParagraph("Vốn góp (VNĐ)");
            foreach (var m in d.HouseholdMembers)
            {
                var row = table.AddRow();
                row.Cells[0].AddParagraph(m.FullName);
                row.Cells[1].AddParagraph(m.DateOfBirth is { } dm ? $"{dm:dd/MM/yyyy}" : "");
                row.Cells[2].AddParagraph(m.IdNumber ?? "");
                row.Cells[3].AddParagraph(m.RelationshipToOwner ?? "");
                row.Cells[4].AddParagraph(m.CapitalContribution is { } c ? $"{c:N0}" : "");
            }
        }

        if (d.FoodSafetyCommitmentAt is { } commitAt)
        {
            OfficialPdfBuilder.AddSpacer(section);
            OfficialPdfBuilder.AddBody(section, $"Đã cam kết bảo đảm điều kiện an toàn thực phẩm lúc {commitAt:dd/MM/yyyy HH:mm}.");
        }

        OfficialPdfBuilder.AddSpacer(section);
        OfficialPdfBuilder.AddBody(section,
            "Tôi xin cam kết: bản thân không thuộc diện pháp luật cấm kinh doanh; trụ sở thuộc quyền sử dụng " +
            "hợp pháp của hộ kinh doanh; hoàn toàn chịu trách nhiệm trước pháp luật về tính hợp pháp, chính xác " +
            "và trung thực của nội dung đăng ký trên.");

        if (IsApproved(d) && d.ReviewedAt is { } reviewedAt)
        {
            OfficialPdfBuilder.AddSpacer(section);
            var byWhom = string.IsNullOrWhiteSpace(d.ReviewedByName) ? "" : $", cán bộ duyệt: {d.ReviewedByName}";
            OfficialPdfBuilder.AddBody(section, $"Đã được {d.WardName} phê duyệt ngày {reviewedAt:dd/MM/yyyy HH:mm}{byWhom}.", bold: true);
        }

        OfficialPdfBuilder.AddSpacer(section);
        OfficialPdfBuilder.AddSignatureBlock(section, ("CHỦ HỘ KINH DOANH", "(Ký và ghi rõ họ tên)"), ("", ""));

        return OfficialPdfBuilder.Render(document);
    }
}
