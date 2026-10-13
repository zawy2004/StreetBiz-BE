# Nâng cấp trải nghiệm StreetBiz Assistant

## v2 — 09/10/2026

Theo master prompt `docs_system/CHATBOT_UPGRADE_V2_MASTER_PROMPT.md` (G1–G3 + F6, F7, F10, F14, F19).

- **Giao diện "Ink & Ember"** (`StreetBiz-FE/src/features/assistant/`): token light/dark riêng cho trợ lý, Be Vietnam Pro, Ember Orb (trạng thái rảnh/tra cứu/nghe/nói), header glass, màn chào theo giờ, composer dạng capsule (nút + chọn/chụp ảnh, nút mic tự đổi thành nút gửi), chữ stream mượt, dải tiến trình tra cứu, thẻ dữ liệu dạng carousel, nguồn dạng chip, sao chép/đọc to/đánh giá/tạo lại, gợi ý hỏi tiếp, chấm báo tin mới trên launcher. Hiệu ứng bằng `motion` (LazyMotion), tôn trọng `prefers-reduced-motion`. Test chạy với mock `motion/react` (`tests/utils/motion-mock.tsx`).
- **Ảnh → món → quầy**: thẻ `dish_match` [AI] với 1–3 ứng viên và mức chắc chắn bằng chữ, "Không phải món này?"; thẻ quầy có tọa độ/giờ mở/khoảng cách/đánh giá (`ChatbotCard.Place`), sắp xếp Phù hợp/Đang mở/Gần nhất/Giá, bản đồ mini Goong (tải khi bấm), nút "Chỉ đường"; bảng tên gọi món có phiên bản.
- **Câu trả lời dữ liệu trực tiếp tự nhiên** (`ChatbotSummaries`): câu tóm tắt theo từng tool, mọi con số chép từ thẻ server.
- **F7** `GET /api/chatbot/briefing`: "Việc cần làm hôm nay" cho hộ kinh doanh/cán bộ, chỉ đọc.
- **F14** tool `ward.slot_permit`: hiệu lực giấy phép theo mã ô, live, trong phường được phân công.
- **F10** chế độ dễ dùng (chữ lớn, giọng nói chậm, từng bước); **F19** đọc to bằng giọng trình duyệt.
- **Voice**: xem [chatbot-voice-design.md](chatbot-voice-design.md). Lượt nói hiện ngay vào luồng chat; trong chế độ nói, quầy hiện dạng dải thẻ ảnh của lần tra cứu mới nhất.
- Đồng ý gửi ảnh có thể ghi nhớ theo tài khoản (D10) và thu hồi trong menu ⋯.

Kiểm chứng: backend Application 496, Infrastructure 314, API 50 (4 live skip) pass; frontend 530 test, typecheck, lint (0 lỗi), build pass. Kiểm tra trực quan trên trình duyệt: menu đính kèm, ô nhập, dark mode và khổ 360px (không tràn ngang), câu hỏi bánh mì với dữ liệu demo. Chưa kiểm tra bằng trình đọc màn hình (người dùng tự thực hiện).

## Đã triển khai (v1)

- Widget 510px, trang đầy đủ, lịch sử dạng cột ở desktop rộng và vùng mở gọn trên mobile. Be Vietnam Pro, typography/phân cấp dữ liệu, bảng cuộn nội bộ, màu theo theme hiện có. Motion CSS, có reduced motion; không thêm thư viện UI.
- Widget là dialog không modal: người dùng tiếp tục dùng trang phía sau bằng bàn phím. Escape đóng, focus quay về launcher. Không khai báo aria-modal khi chưa làm nền inert.
- Lời chào và gợi ý theo role; ngữ cảnh trang có nhãn dễ hiểu và có thể tắt. Context của ảnh không được dùng để bỏ qua phân tích ảnh.
- Ba chế độ `concise`, `detailed`, `steps` được server kiểm tra allowlist. Chế độ đi vào hash idempotency và system instructions; không đổi quyền truy cập. Guest vẫn là hướng dẫn cơ bản, không gọi provider.
- Dữ liệu live dùng thẻ, không lặp nguyên thẻ trong văn bản. Checklist được server xây từ tool đã lấy được dữ liệu, không tự đánh dấu hoàn tất. Khi cần xử lý vẫn mở màn hình nghiệp vụ. Chế độ từng bước mở checklist mặc định.
- Hiển thị tối đa 4 thẻ trước, có mở rộng. Nguồn, thời điểm, nguồn từ ảnh và điều hướng được phân biệt. Sao chép gồm văn bản, thẻ, checklist và nguồn.
- Ảnh được decode/resize tại thiết bị để xem trước. Chỉ upload sau khi đồng ý và gửi. Có chọn file, dán ảnh và kéo thả. Không dùng native confirm để chặn thao tác chọn ảnh.
- Dữ liệu ảnh tạm chỉ nằm trong memory vault có TTL; không lưu ảnh vào lịch sử. Message lưu `hasAttachments` để yêu cầu chọn lại ảnh khi tạo lượt hỏi lại. Request replay cùng ID vẫn theo idempotency hiện có.
- Sau phân tích ảnh, đánh dấu nguồn `IMAGE_ANALYSIS` là nhận định AI chưa xác minh. Không dùng nhãn dữ liệu live. Khi provider ảnh không có/không hoạt động, không chuyển sang model text để đoán ảnh.

## Cấu hình và điều kiện

`Chatbot:AttachmentsEnabled=true` đã bật trong **appsettings.Development.json** để sử dụng ở development; mặc định của class settings vẫn false cho môi trường không cấu hình. API chỉ quảng bá nút ảnh khi chatbot được bật, user đăng nhập và Gemini có model/key cấu hình. Điều này chỉ xác nhận cấu hình, không chứng minh provider đang khỏe. Restart API sau khi thay settings singleton. Production cần kiểm thử provider thật trước khi bật.

Không sửa database trong nâng cấp này. Các trường trình bày mới nằm trong JSON payload hiện có, backward-compatible với payload cũ; request bỏ trống ResponseStyle không thêm field null vào JSON hash cũ.

## Giới hạn dữ liệu đã kiểm tra

API công khai `SearchActiveVendorsQuery` chỉ nhận vị trí/bán kính/take, DTO không có danh mục món hoặc thực đơn. Scaffold có FoodCategory/MenuItem nhưng sự tồn tại của bảng không chứng minh đã có thực đơn được duyệt/công khai. Người dùng xác nhận chưa có dữ liệu thực đơn. `SidewalkSlot.business_category` cũng không đủ để xác nhận món của người bán.

Do đó hiện chỉ nhận diện/giải thích ảnh và dẫn tới Bản đồ người bán bằng action server. Chưa có kết quả lọc quán theo danh mục hay khớp món. Không triển khai marketplace. Để có tìm theo danh mục cần xác định nguồn liên kết danh mục–người bán có chủ sở hữu và trạng thái công khai, rồi duyệt bổ sung query/API; không gán danh mục từ tên quán.

## Voice và chức năng tương lai

Voice chỉ thiết kế, xem [chatbot-voice-design.md](chatbot-voice-design.md). Các tính năng bộ nhớ, camera trực tiếp, hướng dẫn highlight, panel tài liệu tương tác, so sánh và chuyển tiếp nhân viên vẫn là đề xuất chưa triển khai.

## Kiểm thử

Các test bổ sung tập trung vào consent/upload đúng thời điểm, giữ draft khi upload lỗi, không hồi sinh ảnh sau remove, phân quyền CTA, style bị injection, ảnh không bị intent/context bỏ qua, không fallback ảnh sang text, xóa byte ảnh sau xử lý, và lưu/replay metadata đúng.

Kiểm thử hình ảnh thực tế với Gemini và kiểm thử trực quan bằng Browser là hai bước riêng. Không suy ra chúng thành công từ unit tests; môi trường Browser hiện lỗi khởi tạo. Xem kết quả lệnh được báo trong lần bàn giao.

Kết quả vòng nâng cấp: 25 test giao diện chatbot, 109 test Application chatbot, 18 test Infrastructure chatbot và 50 test API đã pass; 2 smoke test cần dịch vụ/database thật được skip. Frontend production build và typecheck thành công; lint 0 lỗi, 3 warning Fast Refresh có sẵn. API được build/test với `-p:BaseOutputPath=bin/ChatbotUpgrade/` vì tiến trình API development đang khóa thư mục build mặc định. Cần tự khởi động lại phiên API development để chạy code mới; không dừng tiến trình của người dùng trong lúc kiểm thử.
