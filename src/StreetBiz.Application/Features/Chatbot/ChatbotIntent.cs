namespace StreetBiz.Application.Features.Chatbot;

/// <summary>Cheap, deterministic shortcuts. Ambiguous/detail questions still go through scoped model tools.</summary>
public static class ChatbotIntent
{
    /// <summary>Only simple explicit dish requests; questions with constraints still use scoped model tools.</summary>
    public static string? FoodQuery(string role, string question)
    {
        if (role != "CUSTOMER") return null;
        var original = question.Normalize(System.Text.NormalizationForm.FormC).Trim();
        var normalized = ChatbotKnowledge.Normalize(original);
        var prefix = new[] { "toi muon an ", "minh muon an ", "em muon an ", "tim quay ban ", "tim quan ban " }
            .FirstOrDefault(normalized.StartsWith);
        if (prefix is null) return null;
        var dish = original[prefix.Length..].Trim().TrimEnd('.', '!', '?').Trim();
        if (dish.Length is < 2 or > 80) return null;
        var words = " " + ChatbotKnowledge.Normalize(dish) + " ";
        if (new[] { " o ", " gan ", " duoi ", " tren ", " khong ", " nao ", " gi ", " hay ", " va ", " hom nay " }.Any(words.Contains)) return null;
        return dish;
    }

    /// <summary>"Ô HC-08 còn hiệu lực không?" from an officer: a live permit lookup by slot code, typed or spoken.</summary>
    public static string? SlotPermit(string role, string question)
    {
        if (role != "WARD_AUTHORITY") return null;
        var q = ChatbotKnowledge.Normalize(question);
        if (!new[] { "giay phep", "hieu luc", "con han", "hop le", "het han" }.Any(q.Contains)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(question, @"(?<![\p{L}\d-])([A-Za-z]{1,4}-?\d{1,4})(?![\p{L}\d-])");
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    public static string? Tool(string role, string question)
    {
        var q = ChatbotKnowledge.Normalize(question);
        // Never mistake requests for instructions or business mutations for a live lookup.
        if (new[] { "lam sao", "huong dan", "cach ", "la gi", "hay duyet", "tu dong", "xoa ", "thanh toan giup" }.Any(q.Contains)) return null;
        if (role != "GUEST" && q.Contains("thong bao")) return "account.notifications";
        return role switch
        {
            "VENDOR" when Has("can dong", "phai tra", "con no", "tai chinh", "khoan nao") => "vendor.finance",
            "VENDOR" when Has("hoa don") => "vendor.invoices",
            "VENDOR" when Has("tien phat", "khoan phat") => "vendor.penalties",
            "VENDOR" when Has("giao dich", "thanh toan cua toi") => "vendor.payments",
            "VENDOR" when Has("hop dong", "sap het han") => "vendor.contracts",
            "VENDOR" when Has("don thue", "ho so thue") => "vendor.rentals",
            "VENDOR" when Has("ho so cua toi", "ho so kinh doanh cua toi", "dang ky cua toi") => "vendor.registrations",
            "VENDOR" when Has("vi pham cua toi") => "vendor.violations",
            "WARD_AUTHORITY" when Has("bao cao thu", "cong no", "doanh thu") && !q.Any(char.IsDigit) => "ward.collection",
            "WARD_AUTHORITY" when Has("tong quan", "tinh hinh phuong", "bao nhieu", "thong ke") => "ward.dashboard",
            "WARD_AUTHORITY" when Has("don thue") => "ward.rentals",
            "WARD_AUTHORITY" when Has("gia han") => "ward.renewals",
            "WARD_AUTHORITY" when Has("ho so", "dang ky") => "ward.registrations",
            "PLATFORM_ADMIN" when Has("kiem duyet", "bi bao cao") => "admin.reports",
            "PLATFORM_ADMIN" when Has("danh muc") => "admin.categories",
            "CUSTOMER" when q.Trim(' ', '?', '.', '!') is "tim nguoi ban tren ban do" or "xem ban do nguoi ban" or "mo ban do" => "public.vendors",
            _ => null,
        };
        bool Has(params string[] terms) => terms.Any(q.Contains);
    }
}
