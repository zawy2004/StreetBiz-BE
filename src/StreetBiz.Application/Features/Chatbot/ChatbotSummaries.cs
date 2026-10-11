using System.Globalization;

namespace StreetBiz.Application.Features.Chatbot;

/// <summary>
/// Natural one-paragraph summaries of live lookups. Every number and status is copied from the
/// server-built cards (never from a model), so the sentence is as authoritative as the cards.
/// </summary>
public static class ChatbotSummaries
{
    private const string Missing = "Chưa ghi nhận";
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public static string? Lead(IReadOnlyList<ChatbotEvidence> live)
    {
        if (live.Count != 1) return null;
        var evidence = live[0];
        var cards = evidence.Cards;
        if (cards.Count == 0) return null;
        var first = cards[0];
        string? V(ChatbotCard card, string label) => card.Fields.FirstOrDefault(f => f.Label == label && f.Value != Missing)?.Value;
        string Count(string status, string noun) => $"{cards.Count(c => V(c, "Trạng thái ghi nhận") == status)} {noun}";
        string Statuses() => string.Join(", ", cards.GroupBy(c => V(c, "Trạng thái ghi nhận") ?? "chưa rõ trạng thái")
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key.ToLower(Vi)}"));
        string Bold(string? value) => value is null ? "" : $"**{value}**";

        switch (evidence.Tool)
        {
            case "vendor.finance":
                var total = V(first, "Tổng phải trả");
                if (total is null) return null;
                if (Amount(total) == 0) return "Hiện bạn **không có khoản phí hay tiền phạt nào phải trả**.";
                var overdue = int.TryParse(V(first, "Số khoản quá hạn"), out var o) ? o : 0;
                return $"Bạn đang cần thanh toán {Bold(total)} (phí {V(first, "Phí phải trả")}, phạt {V(first, "Phạt phải trả")})."
                    + (overdue > 0 ? $" Có **{overdue} khoản quá hạn**." : "")
                    + (V(first, "Hạn gần nhất") is { } due ? $" Hạn gần nhất: **{due}**." : "");
            case "vendor.permit":
                return $"Giấy phép của {first.Title.Replace("Giấy phép của ", "")} đang {Bold(V(first, "Hiệu lực lúc tra cứu"))} lúc tra cứu"
                    + (V(first, "Đến ngày") is { } until ? $", hợp đồng đến ngày {until}." : ".");
            case "vendor.contract":
                return $"{first.Title} (ô {V(first, "Ô")}) đang {Bold(V(first, "Trạng thái"))}, từ {V(first, "Bắt đầu")} đến {V(first, "Kết thúc")}.";
            case "vendor.contracts":
                var active = cards.Where(c => V(c, "Trạng thái ghi nhận") == "Đang hiệu lực").ToArray();
                var soonest = active.Select(c => (Card: c, End: Date(V(c, "Ngày kết thúc")))).Where(x => x.End is not null)
                    .OrderBy(x => x.End).FirstOrDefault();
                return $"Bạn có **{cards.Count} hợp đồng**, trong đó {active.Length} đang hiệu lực."
                    + (soonest.Card is { } c1 ? $" Hợp đồng sắp hết hạn nhất là ô **{V(c1, "Thông tin")}**, kết thúc ngày **{soonest.End:dd/MM/yyyy}**." : "");
            case "vendor.registrations":
                return $"Bạn có **{cards.Count} hồ sơ kinh doanh**: {Statuses()}.";
            case "vendor.registration":
                return $"{first.Title} đang {Bold(V(first, "Trạng thái"))}." + (V(first, "Lý do đã ghi nhận") is { } why ? $" Ghi chú của cán bộ: {why}" : "");
            case "vendor.rentals":
                return $"Bạn có **{cards.Count} đơn thuê ô**: {Statuses()}.";
            case "vendor.rental":
                return $"{first.Title} đang {Bold(V(first, "Trạng thái"))}." + (V(first, "Lý do") is { } reason ? $" Lý do đã ghi nhận: {reason}" : "");
            case "vendor.fees":
                var unpaid = cards.Where(c => V(c, "Trạng thái ghi nhận") is "Chưa thanh toán" or "Quá hạn").ToArray();
                return $"Có **{cards.Count} kỳ phí** gần nhất; {unpaid.Length} kỳ chưa thanh toán"
                    + (unpaid.Length > 0 ? $", tổng **{Money(unpaid.Sum(c => Amount(V(c, "Số tiền"))))}**." : ".")
                    + (cards.Any(c => V(c, "Trạng thái ghi nhận") == "Quá hạn") ? $" Trong đó {Count("Quá hạn", "kỳ")} đã quá hạn." : "");
            case "vendor.penalties":
                return $"Có **{cards.Count} khoản phạt** đã được cán bộ ghi nhận, tổng {Bold(Money(cards.Sum(c => Amount(V(c, "Số tiền")))))}: {Statuses()}.";
            case "vendor.invoices":
                return $"Bạn có **{cards.Count} hóa đơn** đã phát hành, tổng {Bold(Money(cards.Sum(c => Amount(V(c, "Số tiền")))))}.";
            case "vendor.payments":
                return $"Có **{cards.Count} giao dịch** được hệ thống ghi nhận: {Statuses()}.";
            case "vendor.violations":
                return $"Có **{cards.Count} vi phạm** đã được ghi nhận đối với bạn. Mở từng mục để xem chi tiết và quyền giải trình.";
            case "ward.dashboard":
                return $"Phường đang có **{V(first, "Ô đang thuê")}/{V(first, "Tổng ô")} ô** được thuê, **{V(first, "Hồ sơ chờ")} hồ sơ** và **{V(first, "Đơn thuê chờ")} đơn thuê** đang chờ xử lý."
                    + $" Đã thu {V(first, "Đã thu")}; công nợ {V(first, "Công nợ")}.";
            case "ward.collection":
                return $"Từ {V(first, "Từ ngày")} đến {V(first, "Đến ngày")}, phường đã thu {Bold(V(first, "Phí đã thu"))} tiền phí và {Bold(V(first, "Phạt đã thu"))} tiền phạt;"
                    + $" còn {V(first, "Phí chờ thu")} chờ thu và {V(first, "Phí quá hạn")} quá hạn.";
            case "ward.slot_permit":
                return $"{first.Title} đang {Bold(V(first, "Hiệu lực lúc tra cứu"))} lúc tra cứu"
                    + (V(first, "Người bán") is { } vendor ? $", người bán: {vendor}" : "")
                    + (V(first, "Đến ngày") is { } end ? $", hợp đồng đến ngày {end}" : "") + ".";
            case "ward.registrations" or "ward.rentals" or "ward.renewals" or "ward.violations":
                var noun = evidence.Tool switch { "ward.registrations" => "hồ sơ", "ward.rentals" => "đơn thuê", "ward.renewals" => "đề nghị gia hạn", _ => "biên bản" };
                return $"Trang này có **{cards.Count} {noun}** trong phường: {string.Join(", ", cards.GroupBy(c => V(c, "Trạng thái") ?? "chưa rõ").Select(g => $"{g.Count()} {g.Key.ToLower(Vi)}"))}.";
            case "admin.reports":
                return $"Có **{cards.Count} nội dung bị báo cáo** trong trang này: {string.Join(", ", cards.GroupBy(c => V(c, "Trạng thái") ?? "chưa rõ").Select(g => $"{g.Count()} {g.Key.ToLower(Vi)}"))}.";
            default:
                return null;
        }
    }

    /// <summary>Parses the "1.234.000 VND" strings the cards carry.</summary>
    public static decimal Amount(string? value) =>
        value is null ? 0 : decimal.TryParse(new string(value.Where(char.IsDigit).ToArray()), out var amount) ? amount : 0;

    private static string Money(decimal amount) => amount.ToString("N0", Vi) + " VND";

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
