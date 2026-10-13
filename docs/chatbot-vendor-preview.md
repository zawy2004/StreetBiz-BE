# Xem nhanh quầy trong chatbot người mua

- Câu trực tiếp như “Tôi muốn ăn bún chả” tra cứu `public.food` ngay, không cần chờ model quyết định gọi tool. Câu có điều kiện như khu vực/khoảng giá vẫn dùng luồng công cụ hiện có.
- Kết quả công khai có thêm giá niêm yết VND, địa chỉ/khu vực nếu query trả về; không suy đoán dữ liệu thiếu.
- UI gom theo đường dẫn quầy, hiện tối đa ba thẻ ban đầu, có nút xem thêm. Một quầy có nhiều món không bị lặp thẻ.
- Chọn thẻ mở dialog xem nhanh có ảnh, thông tin niêm yết và nút mở trang quầy. Desktop dạng popup; mobile dạng bottom sheet. Không tự điều hướng, không đặt món.
- Dialog native hỗ trợ modal/focus; Escape đóng preview thay vì đóng cả chatbot, trả focus về đúng thẻ đã chọn bằng chuột hoặc bàn phím, kể cả StrictMode. Có reduced-motion.
- Ưu tiên ảnh upload công khai đã kiểm tra; ảnh thiếu/lỗi dùng ảnh minh họa nội bộ cùng helper với trang khám phá. Ghi rõ “Ảnh minh họa” trên cả thẻ và popup, không gọi đây là ảnh thật của quầy. Nếu cả ảnh dự phòng lỗi thì hiển thị placeholder.
- Chỉ nhận đường dẫn quầy public và ảnh upload công khai đã kiểm tra; không render URL ảnh từ Markdown/model. Ảnh minh họa chỉ dùng hiển thị, không đưa vào bằng chứng nhận diện/tìm món của AI.

Đã bổ sung test backend (intent, tìm món không gọi model, thẻ có ảnh/giá) và frontend (popup, điều hướng có chủ ý, gom quầy, mở rộng, ảnh/route không an toàn, Escape, nhãn ảnh minh họa, fallback khi tải lỗi, focus bàn phím/StrictMode).

## Kết quả kiểm chứng ngày 08/10/2026

- Frontend: production build thành công; typecheck thành công; lint không có error, còn 3 warning react-refresh có sẵn ngoài chatbot.
- Frontend: 58 file / 513 test đạt, trong đó 35 test chatbot. Bổ sung mock truy vấn nền trong test cấu hình phường, trang chủ hộ kinh doanh và chi tiết ô giữ chỗ để tránh gọi mạng thật/trả query data undefined.
- Backend: solution build thành công; Application 439, Domain 1, Infrastructure 295, API 50 test đạt. 3 test live opt-in bỏ qua, không gọi provider trả phí hay chạy kịch bản ghi database development. Test commerce database cũng không thực hiện truy vấn khi không có biến kết nối opt-in.
- Cảnh báo không chặn build: bundle bản đồ/3D lớn, annotation PURE trong SignalR; lần biên dịch backend đầy đủ có 3 nullable warning ở ChatRepository ngoài chatbot.
- Browser chưa kiểm chứng trực quan được: công cụ lỗi khởi tạo sandbox (`missing field sandboxPolicy`). DOM/jsdom test không thay thế kiểm tra top-layer, focus trap và bố cục thật trên trình duyệt.
- Không thay đổi database, khóa API hoặc dừng server của người dùng. Output backend nằm riêng trong `bin/ChatbotUpgrade/` để tránh ghi đè DLL của tiến trình đang chạy.

Lệnh kiểm chứng:

```powershell
# StreetBiz-FE
npm run build
npm run lint
npm test -- --maxWorkers=2
# StreetBiz-BE
$env:STREETBIZ_CHATBOT_LIVE_TEST = '0'
$env:STREETBIZ_DB_CONNECTION = ''
dotnet build StreetBiz.Backend.sln --no-restore -p:BaseOutputPath=bin/ChatbotUpgrade/
dotnet test StreetBiz.Backend.sln --no-restore -p:BaseOutputPath=bin/ChatbotUpgrade/
```
