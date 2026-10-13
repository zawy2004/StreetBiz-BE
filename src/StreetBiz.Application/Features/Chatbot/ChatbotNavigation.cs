namespace StreetBiz.Application.Features.Chatbot;

public static class ChatbotNavigation
{
    public static IReadOnlyList<ChatbotAction> ForRole(string role) => role switch
    {
        "VENDOR" => [A("registrations", "Hồ sơ kinh doanh", "/vendor/registrations"), A("slots", "Bản đồ ô thuê", "/vendor/slots"), A("contracts", "Hợp đồng", "/vendor/slots/contracts"), A("finance", "Tài chính", "/vendor/finance"), A("food-safety", "An toàn thực phẩm", "/vendor/store/food-safety")],
        "WARD_AUTHORITY" => [A("dashboard", "Tổng quan phường", "/ward/dashboard"), A("inbox", "Hồ sơ chờ xử lý", "/ward/inbox"), A("reports", "Báo cáo thu", "/ward/reports"), A("scan", "Kiểm tra giấy phép", "/ward/patrol"), A("penalties", "Biểu mức phạt", "/ward/settings/penalties")],
        "PLATFORM_ADMIN" => [A("moderation", "Kiểm duyệt nội dung", "/platform/moderation"), A("categories", "Danh mục", "/platform/categories"), A("accounts", "Quản lý tài khoản", "/platform/accounts")],
        "CUSTOMER" => [A("explore", "Bản đồ người bán", "/customer/explore"), A("scan", "Quét giấy phép", "/customer/scan"), A("account", "Tài khoản", "/account")],
        _ => [A("register", "Đăng ký tài khoản", "/auth/register"), A("signin", "Đăng nhập", "/auth/sign-in"), A("explore", "Bản đồ người bán", "/customer/explore"), A("scan", "Quét giấy phép", "/customer/scan")],
    };

    public static ChatbotAction A(string id, string label, string route) => new(id, label, "NAVIGATE", route);

    public static (string Tool, string Arguments)? ContextTool(string role, ChatbotPageContext? page)
    {
        if (page is null) return null;
        var map = (role, page.PageKey) switch
        {
            ("VENDOR", "vendor.registration") => "vendor.registration",
            ("VENDOR", "vendor.slot") => "vendor.slot",
            ("VENDOR", "vendor.contract") => "vendor.contract",
            ("VENDOR", "vendor.permit") => "vendor.permit",
            ("VENDOR", "vendor.finance") => "vendor.finance",
            ("WARD_AUTHORITY", "ward.registration") => "ward.registration",
            ("WARD_AUTHORITY", "ward.rental") => "ward.rental",
            ("WARD_AUTHORITY", "ward.dashboard") => "ward.dashboard",
            ("PLATFORM_ADMIN", "admin.report") => "admin.report",
            ("CUSTOMER", "public.vendor") => "public.vendor",
            _ => null,
        };
        if (map is null) return null;
        var needsId = map is not ("vendor.finance" or "ward.dashboard");
        if (needsId && (!long.TryParse(page.EntityId, out var id) || id <= 0))
            throw new ChatbotException(400, "invalid_context", "Hãy mở một hồ sơ cụ thể để hỏi về trang này.");
        return (map, needsId ? ChatbotJson.Serialize(new { id = page.EntityId }) : "{}");
    }
}
