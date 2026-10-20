namespace StreetBiz.Application.Features.Chatbot;

/// <summary>Server-owned presentation guidance. No model-generated actions or claims of eligibility.</summary>
public static class ChatbotPresentation
{
    public static string Instruction(string? style) => style switch
    {
        "detailed" => "Cách trình bày: giải thích chi tiết vừa đủ; kết luận trước, rồi lý do, giới hạn và bước tiếp theo. Không lặp dữ liệu của thẻ.",
        "steps" => "Cách trình bày: hướng dẫn theo danh sách đánh số, mỗi bước một việc cụ thể. Nêu việc cần đối chiếu, không coi bước hướng dẫn là đã hoàn tất.",
        _ => "Cách trình bày: ngắn gọn, trả lời thẳng trong vài câu hoặc tối đa 3 ý chính; chỉ mở rộng khi cần để tránh hiểu sai."
    };

    public static IReadOnlyList<ChatbotChecklistItem> Checklist(IReadOnlyList<ChatbotEvidence> evidence)
    {
        var live = evidence.FirstOrDefault(e => e.Source.Kind == "LIVE_DATA" && e.Cards.Count > 0);
        if (live is null) return [];
        var action = live.Cards.Select(c => c.ActionId).FirstOrDefault(id => id is not null);
        return live.Tool switch
        {
            "vendor.registrations" or "vendor.registration" => [
                new("Mở hồ sơ vừa tra cứu để đối chiếu trạng thái và ghi chú của cán bộ.", action),
                new("Nếu có yêu cầu bổ sung, chuẩn bị đúng thông tin được ghi trong hồ sơ."),
                new("Kiểm tra quy trình thuê ô riêng; đăng ký kinh doanh không tự tạo đơn thuê.")],
            "vendor.finance" or "vendor.fees" or "vendor.penalties" => [
                new("Mở khoản thu để kiểm tra số tiền, kỳ thu và hạn thanh toán.", action),
                new("Đối chiếu lịch sử giao dịch trước khi thực hiện thanh toán."),
                new("Kiểm tra trạng thái được máy chủ xác nhận; ảnh chuyển khoản chưa xác nhận thanh toán.")],
            "vendor.contracts" or "vendor.contract" => [
                new("Mở hợp đồng để đối chiếu thời hạn và trạng thái.", action),
                new("Xem chức năng gia hạn hoặc trả ô đang được phép trên hợp đồng."),
                new("Kiểm tra giấy phép trực tiếp khi cần xác minh hiệu lực.")],
            "public.vendors" or "public.vendor" => [
                new("Mở hồ sơ công khai để xem thông tin người bán.", action),
                new("Xác minh giấy phép trực tiếp tại chức năng Quét giấy phép."),
                new("Danh sách công khai chưa xác nhận người bán có món ăn cụ thể.")],
            _ when live.Tool.StartsWith("ward.", StringComparison.Ordinal) => [
                new("Mở dữ liệu vừa tra cứu và đối chiếu hồ sơ hoặc chứng cứ gốc.", action),
                new("Ghi nhận thông tin cần kiểm tra thêm trước khi quyết định."),
                new("Thực hiện quyết định tại màn hình nghiệp vụ bằng tài khoản có thẩm quyền.")],
            _ when live.Tool.StartsWith("admin.", StringComparison.Ordinal) => [
                new("Mở mục vừa tra cứu để xem thông tin đầy đủ.", action),
                new("Đối chiếu quy định vận hành hoặc kiểm duyệt của nền tảng."),
                new("Các quyết định tuân thủ vỉa hè thuộc cán bộ phường.")],
            _ => [new("Mở mục vừa tra cứu để đối chiếu thông tin hiện tại.", action)]
        };
    }
}
