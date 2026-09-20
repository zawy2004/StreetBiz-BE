# Tài khoản thử nghiệm (local development)

Các tài khoản do `docs/dev-seed-demo.sql` tạo ra khi chạy `scripts/setup-local-db.ps1`.

> Chỉ dùng cho máy phát triển cục bộ. Không bao giờ chạy seed này lên cơ sở dữ liệu
> dùng chung hoặc production.

**Mật khẩu của tất cả tài khoản: `Password123!`**

Mật khẩu này thoả BR-59 (tối thiểu 8 ký tự, có chữ thường, chữ hoa, chữ số, ký tự đặc
biệt, không khoảng trắng). Trong cơ sở dữ liệu nó được lưu dưới dạng băm BCrypt work
factor 12, sinh ra bằng chính `PasswordHasher` của dự án — xem phần
[Sinh lại giá trị băm](#sinh-lại-giá-trị-băm).

## Danh sách tài khoản

| # | Số điện thoại | Vai trò | Phường | Dùng để |
|---|---|---|---|---|
| 1 | `0900000001` | PLATFORM_ADMIN | — | Quản trị hệ thống (ADM-xx). Không gắn phường: quản trị viên không phải actor cấp phường. |
| 2 | `0983000001` | WARD_AUTHORITY | 10 — Phường Hải Châu 1 | Duyệt hồ sơ (REG-06, WARD-xx). **`scripts/e2e-auth-onboarding.sh` đăng nhập bằng tài khoản này.** |
| 3 | `0983000002` | WARD_AUTHORITY | 11 — Phường Thanh Khê Đông | Kiểm tra phân tách theo phường: hồ sơ của phường khác trả về 404. |
| 4 | `0983000003` | WARD_AUTHORITY | 12 — Phường An Hải Bắc | Cán bộ phường thứ ba. |
| 5 | `0905000101` | VENDOR | 10 | Hộ kinh doanh **đã được duyệt**: có hợp đồng, giấy phép, lịch phí và gian hàng. |
| 6 | `0905000102` | VENDOR | 10 | Hồ sơ **SUBMITTED, đủ giấy tờ** — phường duyệt được ngay. |
| 7 | `0905000103` | VENDOR | 11 | Hồ sơ **SUBMITTED, thiếu giấy tờ** — minh hoạ cảnh báo chặn duyệt (BR-07). |
| 8 | `0905000104` | VENDOR | 10 | Mang các trạng thái còn lại: UNDER_REVIEW, MORE_INFORMATION_REQUIRED, REJECTED, WITHDRAWN, DRAFT. |
| 9 | `0905000201` | CUSTOMER | 10 | Khách hàng (BUY-xx, DISC-xx). Có đơn hàng đã hoàn tất và đơn bị từ chối. |
| 10 | `0905000202` | CUSTOMER | 12 | Khách hàng thứ hai. Có đơn đang chờ lấy và đơn chờ thanh toán. |

## Hồ sơ đăng ký kinh doanh

Mỗi trạng thái trong `CK_BusinessRegistrations_Status` đều có ít nhất một hồ sơ, để
hàng chờ của phường, danh sách theo dõi của hộ kinh doanh và mọi nhãn trạng thái trên
giao diện đều có dữ liệu thật.

| ID | Hộ kinh doanh | Loại | Phường | Trạng thái | Ghi chú |
|---|---|---|---|---|---|
| 1 | Bánh mì & Xôi Cô Lan | FIXED_STOREFRONT | 10 | APPROVED | Có hợp đồng 1, giấy phép, lịch phí, gian hàng 1 |
| 2 | Xe nước mía Tám Ù | ITINERANT | 10 | SUBMITTED | Đủ giấy tờ → phường thấy đủ 4 hành động |
| 3 | Quán chè Đỗ Mai | FIXED_STOREFRONT | 11 | SUBMITTED | **Không có giấy tờ** → không duyệt được, chỉ còn REVIEW/REJECT/REQUEST_INFO. Có cờ ưu tiên (fast track) |
| 4 | Gánh bún Minh Khôi | ITINERANT | 10 | UNDER_REVIEW | Cán bộ đã nhận xử lý |
| 5 | Xe bánh tráng nướng | ITINERANT | 10 | MORE_INFORMATION_REQUIRED | Có lý do phản hồi; hộ kinh doanh sửa và nộp lại được (REG-04) |
| 6 | Quầy cà phê vỉa hè | FIXED_STOREFRONT | 10 | REJECTED | Có lý do từ chối |
| 7 | Xe hoa quả dạo | ITINERANT | 10 | WITHDRAWN | Hộ kinh doanh tự rút (REG-05) |
| 8 | Quầy đồ chay (nháp) | ITINERANT | 10 | DRAFT | Chưa nộp, chỉ chủ hồ sơ thấy |
| 9 | Bún chả Hải Châu | FIXED_STOREFRONT | 10 | APPROVED | Có hợp đồng 2, gian hàng 2 |

BR-09 (mỗi hộ kinh doanh chỉ có một hồ sơ đang mở) được tôn trọng: hộ số 8 giữ nhiều
hồ sơ nhưng chỉ một hồ sơ ở trạng thái UNDER_REVIEW.

## Dữ liệu kèm theo

- **5 phường** (10, 11, 12, 13, 14) thuộc 4 quận của Thành phố Đà Nẵng.
- **4 khu vực giá** và **27 ô vỉa hè**, trong đó 20 ô `NVL-01..20` nằm trên trục
  đường Nguyễn Văn Linh thật (10 ô mỗi bên đường, cách nhau 15 m, hai dãy cách nhau 38 m).
- **2 hợp đồng thuê** đang hiệu lực kèm giấy phép số, **6 kỳ phí** ở cả ba trạng thái
  PAID / OVERDUE / PENDING, và **2 hoá đơn**.
- **2 gian hàng**, **12 món ăn**, **5 đơn hàng** trải các trạng thái
  COMPLETED / READY_FOR_PICKUP / PREPARING / PENDING_PAYMENT / REJECTED.
- **10 loại vi phạm** và bảng giá phạt cho cả 3 phường có cán bộ.

## Những điều cần lưu ý

- **Mã QR giấy phép là giả.** `qr_payload` của hai giấy phép được seed là chuỗi
  `SEED-PERMIT-CONTRACT-n-DO-NOT-SCAN`, không phải token ký bởi `IPermitTokenService`.
  Quét chúng sẽ không xác thực được. Khi cần thử WARD-11 / BUY-02 thì cấp lại giấy
  phép qua API.
- **File giấy tờ là ảnh/PDF rỗng.** `scripts/setup-local-db.ps1` ghi ra ảnh JPEG 1x1
  và PDF một trang để phần xem tài liệu của cán bộ phường hoạt động. Tên file phải
  đúng 32 ký tự hex (`EvidenceFiles.IsValidFileName`), nếu không API sẽ từ chối URL.
- **Không có SMS thật.** `LoggingSmsSender` in mã OTP ra console của API dưới dạng
  `[DEV-SMS] To 0905123456: Your StreetBiz verification code is 482913.`
  Dùng Git Bash với `tee` để đọc — `Tee-Object` của PowerShell 5.1 ghi UTF-16 và làm
  hỏng `grep`.
- **Tài khoản phường và quản trị không tự đăng ký được.** `RoleCodes.SelfRegisterable`
  chỉ gồm CUSTOMER và VENDOR (BR-02), và không có endpoint tạo tài khoản quản trị.
  Đây là lý do các tài khoản đó phải đến từ seed này.

## Sinh lại giá trị băm

Nếu cần đổi mật khẩu mặc định, băm lại bằng đúng thuật toán của
`src/StreetBiz.Infrastructure/Identity/PasswordHasher.cs` (BCrypt, work factor 12) rồi
thay các chuỗi `password_hash` trong `docs/dev-seed-demo.sql`:

```csharp
// dự án tạm, tham chiếu BCrypt.Net-Next 4.0.3
Console.WriteLine(BCrypt.Net.BCrypt.HashPassword("MậtKhẩuMới!", 12));
```

Không bao giờ tự bịa một chuỗi băm, và không bao giờ đặt mật khẩu dạng văn bản thuần
vào cột `password_hash` — `PasswordHasher.Verify` sẽ trả về `false` và đăng nhập hỏng
mà không báo lỗi rõ ràng.

## Xem thêm

- `docs/testing-auth-vendor-onboarding.md` — kịch bản kiểm thử đầy đủ
- `docs/dev-seed-demo.sql` — chính file seed
- `scripts/setup-local-db.ps1` — dựng lại toàn bộ cơ sở dữ liệu
