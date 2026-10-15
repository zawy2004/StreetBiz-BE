namespace StreetBiz.Application.Features.Chatbot;

/// <summary>
/// What the assistant is for: StreetBiz and nothing else. A deterministic gate that runs before any
/// model is called, so an off-topic question costs no tokens and cannot be argued into an answer;
/// the system prompt repeats the rule for what gets through (a photo, a follow-up, voice).
///
/// It is deliberately a vocabulary check, not a classifier. A false "out of scope" is cheap (the
/// refusal names what the assistant can do); a model asked to be a general chatbot is not.
/// </summary>
public static class ChatbotScope
{
    /// <summary>Words that make a short message a follow-up of the one before it ("còn gì nữa?").</summary>
    private const int FollowUpWords = 8;

    /// <summary>A photo's caption may be loose ("món này là gì?"); the prompt keeps the photo itself on topic.</summary>
    private const int CaptionWords = 12;

    /// <summary>Phrases that try to change the assistant's job. Out of scope even beside a StreetBiz word.</summary>
    private static readonly string[] Hard =
    [
        "bo qua moi huong dan", "bo qua huong dan", "bo qua cac huong dan", "quen het huong dan", "ignore previous",
        "ignore all", "ignore the above", "system prompt", "nhap vai", "roleplay", "role play", "jailbreak",
        "dan mode", "developer mode", "gia vo la", "hay dong vai", "lenh he thong",
    ];

    /// <summary>Tasks that have nothing to do with the site. Out of scope unless the message is also plainly about StreetBiz.</summary>
    private static readonly string[] OffTopic =
    [
        // writing, translating, coding, homework
        "viet code", "lap trinh", "python", "javascript", "typescript", "java", "sql", "html", "css",
        "thuat toan", "giai toan", "bai tap", "phuong trinh", "dich sang", "dich giup", "dich cau", "dich van ban",
        "tieng anh la gi", "viet tho", "lam tho", "ke chuyen", "viet truyen", "viet email", "sang tac",
        "bai hat", "hat mot bai", "ke cho toi nghe", "cau do", "chuyen cuoi",
        // entertainment, news, general knowledge
        "xem phim", "choi game", "bong da", "ty so", "world cup", "thoi tiet", "du bao thoi tiet",
        "tin tuc", "thoi su", "chinh tri", "bau cu", "tong thong", "chien tranh", "thu do cua", "dan so",
        "ai phat minh", "lich su the gioi", "ngoi sao", "ca si", "dien vien",
        // money, health, fortune, private life
        "chung khoan", "co phieu", "bitcoin", "crypto", "tien ao", "xo so", "boi toan", "tu vi", "cung hoang dao",
        "trieu chung", "uong thuoc", "chua benh", "tinh yeu", "nguoi yeu", "tam su", "giam can",
        // cooking at home is not buying food on the site
        "cong thuc nau", "cach nau", "nau an", "che bien mon",
        // other assistants and models
        "chatgpt", "gpt", "openai", "claude", "gemini", "llama", "ban la model nao",
    ];

    /// <summary>A message that names these is about the site even if it also mentions something above.</summary>
    private static readonly string[] Anchors =
    [
        "streetbiz", "via he", "long duong", "ho kinh doanh", "giay phep", "hop dong thue", "o thue", "thue o",
        "dang ky kinh doanh", "ho so kinh doanh", "ho so cua toi", "nguoi ban", "quan an tren", "ban do nguoi ban",
    ];

    /// <summary>Words and phrases (accents removed) that make a message about the site.</summary>
    private static readonly string[] Terms =
    [
        // the product and the account
        "web", "website", "trang web", "ung dung", "app", "nen tang", "tai khoan", "dang nhap", "dang xuat",
        "dang ky", "mat khau", "otp", "xac thuc", "xac minh", "vai tro", "phien dang nhap", "thiet bi", "cai dat",
        "thong tin ca nhan", "ho so", "chuc nang", "tinh nang", "man hinh", "bao mat", "thong bao", "tro ly",
        "chatbot", "giao dien", "ngon ngu", "che do",
        // who uses it
        "ho kinh doanh", "chu ho", "nguoi ban", "nguoi mua", "khach hang", "can bo", "phuong", "quan tri",
        "admin", "vendor", "nguoi dung", "hang rong", "luu dong", "co dinh", "cua hang", "gian hang", "quay hang",
        "quan an", "quan nao", "tim quan", "quan gan", "quan ban", "quay ban", "quay nao", "tim quay",
        // sidewalk rental and compliance
        "via he", "long duong", "o thue", "slot", "vi tri", "ban do", "khu vuc", "tuyen duong", "duong pho",
        "thue", "cho thue", "gia han", "tra o", "chuyen nhuong", "chuyen giao", "chuyen dia diem", "hop dong",
        "giay phep", "qr", "quet ma", "tuan tra", "hien truong", "lan chiem", "vi pham", "xu phat", "tien phat",
        "khoan phat", "khieu nai", "phan anh", "bao cao", "giai trinh", "thu hoi", "dinh chi", "duyet",
        "tu choi", "bo sung", "trang thai", "tien do", "han nop", "han thanh toan", "thoi han", "hieu luc",
        "het han", "gio mo cua", "gio hoat dong", "dien tich", "kich thuoc", "cccd", "giay to", "can cuoc",
        "bien ban", "quyet dinh", "tong quan", "thong ke", "can dong", "phai dong", "phai tra", "con no",
        "khoan nao", "khoan no", "tien no", "sap het han", "tinh hinh", "kiem duyet", "bi bao cao", "danh muc",
        // money
        "phi", "tien", "thanh toan", "hoa don", "cong no", "qua han", "dat coc", "tien coc", "hoan tien",
        "hoan coc", "momo", "zalopay", "bien lai", "giam gia", "khuyen mai", "bang gia", "gia thue", "gia",
        "vnd", "thu phi", "doanh thu", "tai chinh", "giao dich", "lich su giao dich",
        // food and ordering on the site
        "mon an", "mon nao", "mon gi", "mon nay", "mon ngon", "mon chay", "mon nuoc", "mon moi", "tim mon",
        "do an", "do uong", "thuc don", "menu", "don hang", "dat hang", "dat mon", "goi mon", "gio hang",
        "giao hang", "lay hang", "nhan hang", "danh gia", "mo cua", "dang mo", "gan day", "gan toi", "gan nha",
        "uong", "muon an", "an gi", "doi bung", "an sang", "an trua", "an toi", "an vat", "di an",
        // rules a vendor or officer has to follow
        "luat", "nghi dinh", "quy dinh", "dieu kien", "thu tuc", "an toan thuc pham", "attp", "giay chung nhan",
        "phap ly", "muc phat",
        // asking the assistant for help or about itself
        "huong dan", "lam sao", "lam the nao", "cach nao", "giup toi", "giup do", "ho tro", "tro giup",
        "tu van", "xin chao", "chao", "hello", "hi", "hey", "cam on", "thanks", "tam biet", "ban la ai",
        "ban lam duoc gi", "ban giup duoc gi", "co the lam gi", "o dau", "bat dau",
    ];

    /// <summary>
    /// Every curated dish name. A short one is also an ordinary word once accents are gone ("chè" is
    /// "chế", "kem" is "kém"), so inside a longer message only names of two words or five letters
    /// count; a message that is nothing but a dish name is read as a food question whatever its length.
    /// </summary>
    private static readonly Lazy<string[]> DishNames = new(() =>
        ChatbotFoodPhoto.KnownDishNames().Select(Pad).Select(p => p.Trim()).Where(n => n.Length >= 2).Distinct().ToArray());

    private static readonly Lazy<string[]> DishPhrases = new(() =>
        DishNames.Value.Where(n => n.Contains(' ') || n.Length >= 5).ToArray());

    /// <param name="question">What the user typed or said.</param>
    /// <param name="hasEarlierTurn">The conversation already holds an answered turn.</param>
    /// <param name="hasAttachment">A photo came with the message.</param>
    /// <param name="hasPageContext">The app told the assistant which screen the user is on.</param>
    public static bool InScope(string question, bool hasEarlierTurn = false, bool hasAttachment = false, bool hasPageContext = false)
    {
        var padded = Pad(question);
        var words = padded.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words == 0) return false;
        if (Mentions(padded, Hard)) return false;

        var anchored = Mentions(padded, Anchors);
        if (!anchored && Mentions(padded, OffTopic)) return false;
        if (anchored || Mentions(padded, Terms) || Mentions(padded, DishPhrases.Value)) return true;
        if (DishNames.Value.Contains(padded.Trim())) return true;

        if (hasAttachment && words <= CaptionWords) return true;
        return (hasEarlierTurn || hasPageContext) && words <= FollowUpWords;
    }

    /// <summary>The fixed reply: what the assistant is for, and what to ask instead. No model, no data.</summary>
    public static string Refusal(string role)
    {
        var help = role switch
        {
            "VENDOR" =>
                "hồ sơ đăng ký kinh doanh, thuê ô vỉa hè và hợp đồng, giấy phép và mã QR, phí và hóa đơn, tiền phạt, hoặc cách dùng các chức năng của StreetBiz.",
            "WARD_AUTHORITY" =>
                "hàng đợi hồ sơ, đơn thuê và gia hạn, tình hình thu phí và vi phạm của phường, hoặc cách dùng các màn hình nghiệp vụ.",
            "PLATFORM_ADMIN" =>
                "kiểm duyệt nội dung, danh mục món, quản lý tài khoản và vận hành nền tảng.",
            "CUSTOMER" =>
                "tìm quán và món đang bán, người bán gần bạn, đơn hàng, thanh toán, đánh giá và khiếu nại.",
            _ =>
                "cách đăng ký tài khoản, xem bản đồ người bán, tìm món ăn và cách dùng StreetBiz.",
        };
        return "Mình là trợ lý của StreetBiz nên chỉ hỗ trợ các nội dung liên quan đến website này, chưa thể trả lời câu hỏi đó.\n\n"
            + "Bạn có thể hỏi mình về " + help;
    }

    private static bool Mentions(string padded, IEnumerable<string> terms) =>
        terms.Any(term => padded.Contains(" " + term + " ", StringComparison.Ordinal));

    /// <summary>Lower case, no accents, letters and digits only, one space between words, a space at each end.</summary>
    private static string Pad(string text)
    {
        var folded = ChatbotKnowledge.Normalize(text);
        var builder = new System.Text.StringBuilder(folded.Length + 2).Append(' ');
        var gap = true;
        foreach (var ch in folded)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                gap = false;
            }
            else if (!gap)
            {
                builder.Append(' ');
                gap = true;
            }
        }

        if (!gap) builder.Append(' ');
        return builder.ToString();
    }
}
