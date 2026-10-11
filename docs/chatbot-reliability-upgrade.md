# Chatbot — độ tin cậy và tìm món bằng ảnh (08/10/2026)

## Cấu hình demo

`appsettings.Development.json` (local, không commit secrets) bật:

```json
"Chatbot": {
  "AttachmentsEnabled": true,
  "EnforceDailyTokenBudget": false,
  "IndependentKeyQuotas": true,
  "ProviderTimeoutSeconds": 55,
  "TurnTimeoutSeconds": 120
}
```

Không cần thay đổi schema, xóa dữ liệu hay reset bộ đếm cũ. Khởi động lại backend để nạp settings.
Production vẫn mặc định bật daily admission và không giả định các key độc lập quota.
Giữ giới hạn đồng thời, tool/model steps, kích thước ảnh và deadline; bỏ daily admission không có nghĩa vòng lặp vô hạn.
Usage thành công được đối soát theo provider; usage bị thiếu hoặc ngắt vẫn giữ ước lượng, không phải hóa đơn.

## Pool và phân tuyến

- Văn bản ưu tiên Groq kể cả câu dài; Gemini dự phòng trước khi có văn bản.
- Ảnh chỉ dùng Gemini. Không gửi ảnh cho adapter Groq text-only.
- Round-robin hiện hữu, tối đa 6 credential thử trong một lần gọi; key 401/403 nghỉ riêng 5 phút.
- 429: tôn trọng Retry-After (delta hoặc ngày HTTP). Khi `IndependentKeyQuotas=true`, nghỉ key đó và thử key khác. Khi false, nghỉ cả provider vì quota có thể dùng chung.
- Lỗi HTTP >=500/network/header timeout thử key kế tiếp có giới hạn; lỗi payload/model không xoay vô ích. Không đổi key giữa một câu trả lời đã stream.
- Timeout chờ headers mỗi key tối đa 20 giây, vẫn chịu deadline tổng. Một lượt không đảm bảo thử hết mọi key nếu deadline hết.
- Gemini 3 dùng thinkingLevel low để giảm độ trễ; không yêu cầu thought text. Tham khảo [Google thinking](https://ai.google.dev/gemini-api/docs/generate-content/thinking).
- Không log key, URL có key, raw provider response, nội dung câu hỏi hay ảnh.

## Tìm món từ ảnh — chỉ đọc dữ liệu hiện có

Codebase đã có query công khai `SearchMarketplaceMenuQuery` và `ListStorefrontsQuery`. Tool `public.food` của CUSTOMER tái sử dụng chúng; không tạo thực đơn, giỏ hàng, đơn hàng hay chức năng commerce mới.

1. Gemini xem ảnh + câu hỏi. Không chắc tên món thì hỏi lại.
2. Tool tìm tên món/tên quầy/danh mục/mô tả được query hiện có hỗ trợ; tối đa 8 món + 8 quầy. Không suy ra toàn hệ thống từ danh sách giới hạn.
3. Chọn tối đa 3 ảnh thật do query công khai trả về. Chỉ đọc `/api/uploads/menu-images/{owner}/{random-name}` từ adapter storage hiện có, kiểm tra tên/signature và giới hạn 2 MB/ảnh.
4. Gemini nhận ảnh người dùng và các ảnh ứng viên có nhãn để đối chiếu. Không tải URL tùy ý, ảnh hồ sơ riêng tư, hay ảnh minh họa stock của FE.
5. Trả thẻ với lý do khớp, ảnh công khai nếu hợp lệ và link `/customer/explore/stores/{id}` do server tạo. Không khẳng định còn món hoặc món ảnh chắc chắn giống món niêm yết.

Giới hạn: tìm kiếm văn bản trước rồi đối chiếu tập ứng viên nhỏ, chưa phải chỉ mục tìm ảnh toàn kho. Nếu không có tên/mô tả liên quan hoặc chưa có dữ liệu public thì có thể không tìm được. Ảnh từ CDN/ngoài storage, ảnh >2 MB hoặc hỏng bị bỏ qua; không nói đã so ảnh khi chưa được cung cấp. Không dùng ảnh minh họa stock để chứng minh quầy bán món.

## UX, quyền và quan sát

- Dùng lại câu trả lời hợp lệ từ bước chọn tool, không gọi model lại vô ích. Dữ liệu private/finance/permit vẫn qua formatter của server.
- Lịch sử kết quả tìm món giữ ngữ cảnh hội thoại nhưng bắt buộc tra cứu lại dữ liệu hiện tại. Khi ngữ cảnh dài, bỏ cặp hội thoại cũ trước, giữ cặp gần nhất, quyền và bằng chứng hiện tại. Không tự suy ra vị trí người dùng.
- Status nhận diện/tìm kiếm/đối chiếu; phân loại timeout, quota/cooldown, lỗi key/model, blocked, hết output, stream ngắt.
- Khi lỗi và nhận được phản hồi server, FE giữ tối đa một ảnh lỗi trong RAM 5 phút, gắn request ID; retry cần xác nhận consent lại. Không lưu ảnh vào DB/localStorage/lịch sử. Reload/logout hết ảnh tạm.
- Meter thêm `chatbot.provider.errors` theo provider/category, không có dữ liệu người dùng. Kết nối collector là công việc vận hành riêng.

## Kiểm tra

Test hồi quy cho daily admission bật/tắt, settle usage, replay/ownership, independent/shared quota, cooldown, Gemini finish reasons, đường dẫn ảnh private/SSRF, bounded image reads, tool schema, pipeline ảnh → truy vấn → so ảnh, kiểu trả lời và UI retry/consent/thumbnail.
Smoke opt-in riêng `Configured_providers_stream_synthetic_public_question` và `Configured_vision_accepts_public_demo_photo` không ghi DB; test thứ hai gửi ảnh minh họa công khai trong repo sang Gemini. Không chạy toàn bộ live SQL test chỉ để kiểm tra provider.

Kết quả trực tiếp 08/10/2026: Groq/Gemini stream câu chào giả lập thành công; Gemini nhận ảnh minh họa bún chả, gọi `public.food`, nhận tool response giả lập và trả lời thành công. Đây là kiểm tra giao thức/kết nối, không phải chứng nhận độ chính xác nhận diện hay nghiệm thu kết quả với dữ liệu quầy thật. Build frontend, typecheck và các test chatbot đã qua; lint còn 3 warning cũ ngoài chatbot. Chưa kiểm thử trực quan/E2E trình duyệt trong lượt này.
