# Tối ưu hỏi ảnh — 08/10/2026

## Hành vi

- Người mua hỏi tìm món từ ảnh: Gemini nhận diện tên phổ biến rồi gọi `public.food`. Máy chủ tìm dữ liệu công khai và trả ngay tên nhận diện cùng thẻ quầy; không gửi lại ảnh để model viết một câu trả lời thứ hai.
- Dùng `gemini-3.5-flash-lite` với thinking `minimal` cho bước tìm món đơn giản. `Chatbot:Gemini:FoodVisionModel` có thể ghi đè; đặt chuỗi rỗng để dùng model chính. Nếu model tùy chọn không tồn tại/không hỗ trợ payload (lỗi cấu hình), thử model chính trong cùng deadline.
- Model chính vẫn dùng cho câu hỏi có yêu cầu “so sánh”/“đối chiếu” và phân tích khác. Gemini 3.8 giữ thinking `low`; không gửi `minimal` vì model đó không hỗ trợ mức này.
- Câu hỏi ảnh chỉ giữ cặp lịch sử gần nhất. Ảnh chưa rõ thì model phải hỏi lại, không ép gọi công cụ hoặc đoán chắc loại nhân/thành phần.
- Không có kết quả cho tên cụ thể như “bánh mì xíu mại” thì tìm rộng một lần theo nhóm món đã định nghĩa, ví dụ “bánh mì”. Hiển thị rõ đây là quầy liên quan, chưa xác nhận đúng biến thể. Không bỏ điều kiện phủ định trong từ khóa tìm kiếm.
- Chỉ khi người dùng yêu cầu so sánh/đối chiếu mới đọc các ảnh niêm yết công khai để gửi thêm cho Gemini. Ảnh minh họa của frontend không dùng làm chứng cứ.

## Phản hồi bị gián đoạn

- Câu trả lời ảnh cuối dùng `generateContent` thay vì SSE; kiểm tra `finishReason=STOP` và nội dung trước khi phát ra UI. Bước trả lời cuối đặt function calling `NONE` để tránh model gọi thêm công cụ.
- Application cũng đệm nội dung ảnh trước khi phát, thử lại tối đa một lần với lỗi ngắt phản hồi, đọc mạng/JSON lỗi, timeout hoặc function call lỗi. Mỗi lần gọi lại dùng round-robin của pool key hiện hữu.
- Không thử lại safety/blocked. Quota HTTP vẫn dùng cơ chế xoay key hiện hữu trong provider; không thêm vòng retry quota bên ngoài. Người dùng hủy hoặc deadline toàn lượt hết thì không tiếp tục.
- `Chatbot:ImageProviderTimeoutSeconds` mặc định 25 giây, không vượt deadline provider/turn. Tác vụ model ảnh nhanh chờ headers tối đa 12 giây/key; các tác vụ khác vẫn tối đa 20 giây/key. Đây là deadline, không phải cam kết tốc độ thực tế.
- Mọi attempt vẫn được tính vào giới hạn token một lượt. Không phát đoạn dở dang của lần thất bại rồi nối với câu trả lời mới.
- Nếu diễn giải ảnh thất bại sau khi tìm dữ liệu thành công, vẫn lưu và hiện thẻ quầy public cùng nguồn và đường dẫn server đã kiểm tra. Nội dung lỗi không còn khẳng định mọi trường hợp đều do mất kết nối.
- Không thay schema/database; không sửa API keys, không dừng tiến trình đang chạy. Backend đang chạy cần khởi động lại để nạp bản code mới.

## Kiểm chứng

- Backend solution build thông qua toàn bộ bộ test: Domain 1, Application 454, Infrastructure 306, API 50 đạt; 4 test live opt-in bỏ qua trong suite offline (811 test đạt).
- Có regression cho trả lời dở dang, retry có giới hạn, hủy request, safety/quota, giữ thẻ quầy, tìm rộng, finish reason, model routing/fallback và thought signatures.
- Smoke Gemini với ảnh `StreetBiz-FE/public/images/food/banh-mi.jpg` nhận diện tên bánh mì và gọi `public.food` đúng trong một lượt. Đo ban đầu model chính 24,58 giây; sau chuyển Flash-Lite 1,83 giây. Đây là số đo hai lượt với một ảnh mẫu, không phải benchmark nhiều ảnh hay thời gian toàn bộ UI.
- Smoke so sánh ảnh nâng cao với model chính có một lượt timeout 55 giây ở dịch vụ bên ngoài; kiểm tra lại thành công trong khoảng 41 giây cho cả hai lượt model. Luồng này vẫn chậm hơn luồng tìm món thông thường và chỉ chạy khi yêu cầu đối chiếu. Không coi bộ test giả lập là bảo đảm dịch vụ cloud luôn khả dụng.

Tham khảo chính thức: [Gemini Flash-Lite](https://ai.google.dev/gemini-api/docs/models/gemini-3.5-flash-lite), [thinking levels](https://ai.google.dev/gemini-api/docs/thinking.md), [function calling](https://ai.google.dev/gemini-api/docs/generate-content/function-calling?authuser=0&hl=en).
