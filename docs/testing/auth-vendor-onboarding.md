# Hướng dẫn kiểm thử: Authentication & Vendor Onboarding

Tài liệu này hướng dẫn chạy và kiểm thử toàn bộ hai module **Authentication**
(AUTH-01…09) và **Vendor Onboarding** (REG-01…05) trên cả StreetBiz-BE và
StreetBiz-FE, ở máy local.

Có 3 cách kiểm thử, nên làm theo thứ tự:

| Cách | Mất bao lâu | Dùng khi |
|---|---|---|
| [Test tự động](#3-kiểm-thử-tự-động) | ~2 phút | Sau mỗi lần sửa code |
| [Test thủ công trên giao diện](#4-kiểm-thử-thủ-công-trên-giao-diện) | ~30 phút | Trước khi demo / nộp bài |
| [Test API trực tiếp](#6-test-api-không-qua-giao-diện) | tuỳ | Khi cần khoanh vùng lỗi là FE hay BE |

---

## 1. Chuẩn bị (làm một lần)

Phần mềm cần có:

- .NET SDK 8 trở lên, `dotnet-ef` (`dotnet tool install --global dotnet-ef`)
- SQL Server LocalDB (đi kèm Visual Studio hoặc SQL Server Express)
- Node.js 20 trở lên
- Git Bash (để chạy script test end-to-end)
- Một ảnh JPG/PNG bất kỳ dưới 5 MB, dùng làm "ảnh CCCD" khi test

### 1.1 Tạo database local

StreetBizDB theo hướng database-first, và migration `InitialBaseline` cố ý để
trống (xem [database.md](../database.md)). Schema chuẩn nằm ở
`db/StreetBiz_SQL_Server.sql`. Script dưới đây tạo lại toàn bộ database trên
LocalDB từ schema đó, rồi nạp dữ liệu tham chiếu và bộ dữ liệu demo:

```powershell
cd StreetBiz-BE
powershell -ExecutionPolicy Bypass -File scripts/setup-local-db.ps1 -Recreate
```

Kết quả đúng:

```
tables=52  triggers=5  views=2  checks=78  migrations=2
Wards available: 5
accounts=10  registrations=9  slots=27  contracts=2  storefronts=2  orders=5
```

> ⚠️ `-Recreate` **xoá toàn bộ dữ liệu** của StreetBizDB rồi dựng lại. Không có
> `-Recreate`, script từ chối đụng vào một database đã có bảng. Sao lưu trước nếu
> cần:
> `sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "BACKUP DATABASE [StreetBizDB] TO DISK='C:\Temp\StreetBizDB.bak' WITH INIT"`

> Hãy **tắt API** trước khi chạy: một kết nối đang mở sẽ chặn `DROP DATABASE`.

Khác với trước đây, schema này có **đầy đủ** 5 trigger, 2 view và 78 CHECK
constraint của DB thật — nên DB local giờ từ chối đúng những dữ liệu mà DB thật
từ chối (ví dụ hai hợp đồng trùng ngày trên cùng một ô, hoặc `unit_type` ngoài
PROVINCE/DISTRICT/WARD). Script chạy hai file theo thứ tự:

| Thứ tự | File | Nội dung |
|---|---|---|
| 1 | `db/StreetBiz_SQL_Server.sql` | Schema (51 bảng, 5 trigger, 2 view, CHECK constraint), dữ liệu tham chiếu (4 vai trò, 10 đơn vị hành chính gồm 5 phường, 15 loại vi phạm) và đóng dấu `__EFMigrationsHistory` |
| 2 | `db/StreetBiz_Demo_Seed.sql` | 10 tài khoản, 9 hồ sơ đăng ký đủ 7 trạng thái, ô vỉa hè, hợp đồng, gian hàng, đơn hàng |

Tài khoản đăng nhập: xem [database.md](../database.md#demo-data) — tất cả
dùng mật khẩu `Password123!`.

### 1.2 Cấu hình frontend

Trong `StreetBiz-FE/.env`:

```dotenv
VITE_API_BASE_URL=http://localhost:5023/api
VITE_USE_MOCK_API=false
```

`VITE_USE_MOCK_API=true` sẽ chạy FE bằng dữ liệu giả, không cần BE. Chế độ này
không dùng để test tích hợp.

---

## 2. Chạy hệ thống

Mở 2 cửa sổ terminal.

**Terminal 1: Backend** (dùng Git Bash để lưu log, script test cần đọc OTP từ log):

```bash
cd StreetBiz-BE
dotnet run --project src/StreetBiz.API 2>&1 | tee api.log
```

Chờ đến dòng `Now listening on: http://localhost:5023`. Swagger ở
http://localhost:5023/swagger.

**Terminal 2: Frontend:**

```powershell
cd StreetBiz-FE
npm install
npm run dev
```

Mở địa chỉ Vite in ra (thường là http://localhost:5173).

> **OTP ở đâu?** Môi trường dev không gửi SMS thật. Mã được in ra terminal của
> API, dạng:
> `[DEV-SMS] To 0905123456: Your StreetBiz verification code is 482913.`

---

## 3. Kiểm thử tự động

### 3.1 Unit test backend

```powershell
cd StreetBiz-BE
dotnet test StreetBiz.Backend.sln
```

Kỳ vọng: tất cả `Passed!`. `StreetBiz.Application.Tests` kiểm tra các quy tắc
nghiệp vụ: OTP cooldown không làm lộ số đã đăng ký, sai mật khẩu hiện tại trả về
lỗi theo trường, phường không hợp lệ, BR-09 khi gửi lại hồ sơ, chỉ gắn được giấy
tờ khi hồ sơ còn sửa được và file thuộc chính người dùng, chặn URL `blob:`.

### 3.2 Test frontend

```powershell
cd StreetBiz-FE
npm run typecheck
npm run lint
npm test
```

Kỳ vọng: typecheck không lỗi, lint `0 errors` (3 cảnh báo fast-refresh có từ
trước), tất cả test pass. Test FE luôn chạy ở chế độ mock, không cần BE.

### 3.3 Test end-to-end qua HTTP (BE thật + DB thật)

Khi API đang chạy như mục 2 (có file `api.log`), mở **Git Bash** khác:

```bash
cd StreetBiz-BE
bash scripts/e2e-auth-onboarding.sh
```

Kỳ vọng: dòng cuối `RESULT: 59 passed, 0 failed`. Script tự tạo 2 tài khoản
vendor với số điện thoại ngẫu nhiên, rồi chạy toàn bộ luồng, gồm cả các trường
hợp lỗi:

- đăng ký, OTP cooldown (429), số đã đăng ký (409), phường không tồn tại (400);
- đăng nhập, quên mật khẩu trả kết quả giống nhau cho mọi số điện thoại;
- đăng nhập bằng OTP (mã dùng một lần), số `+84…` vào đúng tài khoản `0…`;
- upload file hợp lệ / giả mạo / quá 5 MB, người khác không tải được file;
- nộp, xem chi tiết, BR-09 khi gửi lại, rút hồ sơ;
- Phường xét duyệt hồ sơ: mở hàng chờ, yêu cầu bổ sung, chặn ghi đè khi hồ sơ đã
  đổi trạng thái (409), vendor không vào được hàng chờ của Phường (403);
- token bị thu hồi (đăng xuất, đăng xuất thiết bị khác) bị từ chối **ngay lập tức**.

Script cần một tài khoản cán bộ phường 10 đang tồn tại (mặc định `0983000001` do
`db/StreetBiz_Demo_Seed.sql` tạo); đổi bằng biến `WARD_PHONE` / `WARD_PW`.

---

## 4. Kiểm thử thủ công trên giao diện

Mỗi mục là một kịch bản: làm theo cột **Thao tác**, đối chiếu cột **Kết quả mong
đợi**. Nên dùng một số điện thoại mới cho mỗi lần test đăng ký (VD `0905xxxxxx`).
Trong ô số điện thoại, nhập **9 số sau +84** (VD `905123456`), hoặc dán cả số
`0905123456`.

### 4.1 Đăng ký tài khoản (AUTH-01, AUTH-02)

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | Trang đăng nhập → **Đăng ký ngay**. Bấm **Tiếp tục & Nhận OTP** khi form trống | Báo lỗi dưới ô Họ tên và Số điện thoại; không chuyển trang |
| 2 | Gõ mật khẩu `abc` | Hiện danh sách 6 điều kiện mật khẩu, tick xanh các điều kiện đã đạt |
| 3 | Nhập mật khẩu `Str0ng!Pass`, ô nhập lại khác đi | "Mật khẩu nhập lại không khớp." |
| 4 | Chọn **Hộ kinh doanh**, chọn một phường, nhập đúng mọi trường → gửi | Sang màn **Xác thực số điện thoại**; URL chỉ có `?purpose=REGISTRATION`, **không chứa mật khẩu**; terminal API có dòng `[DEV-SMS]` |
| 5 | Nhập sai mã OTP | "Mã xác thực không đúng. Vui lòng thử lại." |
| 6 | Bấm **Gửi lại mã** ngay | Nút bị khoá, đếm ngược 60s |
| 7 | Bấm **Quay lại**, điền lại form (cùng số) và gửi trong vòng 60s | Vẫn sang màn xác thực, đếm ngược tiếp số giây còn lại; mã cũ vẫn dùng được |
| 8 | Nhập đúng mã | Toast "Tạo tài khoản thành công", vào trang chủ Hộ kinh doanh |
| 9 | Đăng xuất, đăng ký lại bằng **cùng số** | Lỗi ngay dưới ô số điện thoại "Số điện thoại này đã được đăng ký…" kèm link **Đăng nhập bằng số này** (không phải nhập OTP) |
| 10 | Đang ở màn xác thực thì bấm F5 | Quay về màn đăng ký (dữ liệu tạm chỉ nằm trong bộ nhớ) |

### 4.2 Đăng nhập / đăng xuất (AUTH-03, AUTH-04)

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | Nhập số `123` | "Số điện thoại phải gồm 10 số, bắt đầu bằng 0." |
| 2 | Sai mật khẩu | "Số điện thoại hoặc mật khẩu không đúng." (không tiết lộ số có tồn tại hay không) |
| 3 | Đúng thông tin | Vào trang chủ theo vai trò. Tài khoản Người mua → `/customer/explore`, Hộ kinh doanh → `/vendor/home` |
| 4 | Người mua gõ thẳng URL `/vendor/registrations` | Bị chuyển về trang của Người mua |
| 5 | F5 trang | Vẫn đăng nhập |
| 6 | Tài khoản → **Đăng xuất** | Về trang đăng nhập; mở DevTools → Application → Local Storage: key `streetbiz-tokens` đã bị xoá |
| 7 | Xem trang đăng nhập và trang Tài khoản | Không có khối **TÀI KHOẢN DEMO** / **Đổi vai trò (demo)** (chỉ hiện ở chế độ mock) |

### 4.3 Quên & đặt lại mật khẩu (AUTH-05, AUTH-06)

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | **Quên mật khẩu?** → nhập một số **chưa đăng ký** → gửi | Vẫn sang màn nhập mã (không báo "không tìm thấy tài khoản"); terminal API **không** có dòng `[DEV-SMS]` |
| 2 | Làm lại với số **đã đăng ký** | Có dòng `[DEV-SMS]` trong terminal API |
| 3 | Nhập mã **sai** → sang màn đặt mật khẩu → nhập mật khẩu mới hợp lệ → **Xác nhận** | Báo mã không đúng, hiện nút **Nhập lại mã OTP** đưa về màn nhập mã |
| 4 | Nhập mã đúng, mật khẩu mới `N3w!Passw0rd` | Toast thành công, về trang đăng nhập |
| 5 | Đăng nhập bằng mật khẩu cũ / mới | Cũ: sai. Mới: vào được |
| 6 | Nếu trước đó có trình duyệt khác đang đăng nhập tài khoản này | Trình duyệt đó bị đăng xuất ở thao tác kế tiếp (đặt lại mật khẩu thu hồi mọi phiên) |

### 4.4 Đổi mật khẩu & phiên đăng nhập (AUTH-07, AUTH-08, AUTH-09)

Cần **2 trình duyệt** (hoặc một cửa sổ thường + một cửa sổ ẩn danh), cùng đăng
nhập một tài khoản. Gọi là A và B.

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | Ở A: Tài khoản → **Phiên đăng nhập** | Thấy 2 phiên; phiên của A có nhãn **ĐANG DÙNG** |
| 2 | Ở A: bấm nút xoá ở phiên của B | Toast "Đã đăng xuất thiết bị", danh sách còn 1 |
| 3 | Ở B: bấm sang một trang có gọi API (VD **Đăng ký kinh doanh**) | B bị đưa về đăng nhập, kèm thông báo "Phiên đăng nhập đã hết hạn" |
| 4 | Đăng nhập lại ở B. Ở A: **Đổi mật khẩu** với mật khẩu hiện tại **sai** | Lỗi hiện **dưới ô Mật khẩu hiện tại**, A vẫn đăng nhập |
| 5 | Ở A: đổi mật khẩu với mật khẩu mới trùng mật khẩu cũ | "Mật khẩu mới phải khác mật khẩu hiện tại." |
| 6 | Ở A: đổi mật khẩu đúng | Toast "…Các thiết bị khác đã được đăng xuất."; A vẫn dùng bình thường |
| 7 | Ở B: thao tác bất kỳ có gọi API | B bị đăng xuất |

### 4.5 Nộp hồ sơ đăng ký kinh doanh (REG-01, REG-02)

Đăng nhập bằng tài khoản **Hộ kinh doanh** → mục **Đăng ký kinh doanh** →
**Đăng ký kinh doanh mới**.

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | Bước 1 chọn **Cửa hàng cố định** → Tiếp tục. Bước 2 để trống → Tiếp tục | Báo lỗi tên hộ kinh doanh, phường, địa chỉ |
| 2 | Quay lại bước 1 chọn **Bán hàng lưu động** | Ở bước 2, địa chỉ thành "không bắt buộc", ẩn ô vĩ độ/kinh độ |
| 3 | Chọn lại **Cửa hàng cố định**, điền đủ (có thể nhập vĩ độ `16.0678`, kinh độ `108.2208`) → Tiếp tục | Sang bước 3; có 2 ô ảnh: **CCCD gắn chip** và **Giấy phép kinh doanh** |
| 4 | Bấm **Nộp hồ sơ** khi chưa chọn ảnh | Viền đỏ ở ô ảnh còn thiếu, báo lỗi |
| 5a | Chọn một ảnh lớn hơn 5 MB | Báo ngay "Dung lượng file tối đa 5 MB.", ảnh không được nhận |
| 5b | Đổi đuôi một file `.txt` thành `.jpg`, chọn file đó, chọn đủ ảnh còn lại → **Nộp hồ sơ** | Trình duyệt tin đuôi file nên cho chọn, nhưng khi nộp BE kiểm tra nội dung file và báo "Chỉ chấp nhận ảnh JPG, PNG, WEBP hoặc file PDF."; **không** có hồ sơ nào được tạo. Thay bằng ảnh thật rồi nộp lại |
| 6 | Chọn 2 ảnh hợp lệ → **Nộp hồ sơ** | Nút quay vòng, sau đó toast "Đã nộp hồ sơ đăng ký", về danh sách; hồ sơ có nhãn **ĐÃ NỘP** |
| 7 | Kiểm tra thư mục `StreetBiz-BE/src/StreetBiz.API/App_Data/uploads/evidence/<userId>/` | Có 2 file tên dạng `32 ký tự hex.jpg` |
| 8 | Nộp thêm một hồ sơ mới khi hồ sơ trước vẫn đang chờ | "Bạn đang có một hồ sơ chờ xét duyệt…" (BR-09) và **không** tạo thêm hồ sơ |
| 9 | Kiểm tra khả năng làm lại sau lỗi: ở bước 3, tắt API (Ctrl+C) rồi bấm **Nộp hồ sơ** → báo lỗi kết nối → bật lại API → bấm **Nộp hồ sơ** lần nữa | Chỉ có **một** hồ sơ trong danh sách, đủ giấy tờ |

### 4.6 Theo dõi, sửa, rút hồ sơ (REG-03, REG-04, REG-05)

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | Bấm vào hồ sơ vừa nộp | Trang chi tiết: loại hình, ngày nộp, địa chỉ; mục **Giấy tờ minh chứng** hiện ảnh thật (không phải ô trống); bấm ảnh để mở to |
| 2 | Copy link ảnh (chuột phải → Copy image address) mở sang tab khác | Link là `blob:` và chỉ xem được trong tab này. Gọi thẳng `/api/uploads/evidence/...` mà không có token sẽ bị 401 |
| 3 | Bấm **Chỉnh sửa** | Wizard mở với dữ liệu cũ, kể cả vĩ độ/kinh độ; bước 3 **không bắt buộc** chọn lại ảnh, có thêm ô **Giấy tờ địa chỉ** |
| 4 | Đổi tên → **Cập nhật & Gửi lại** | Toast thành công, tên mới hiện trong danh sách |
| 5 | Mở hồ sơ → **Rút hồ sơ** → xác nhận | Toast "Đã rút hồ sơ đăng ký", về danh sách với nhãn **ĐÃ RÚT**. Mở lại hồ sơ: không còn nút Chỉnh sửa / Rút hồ sơ |
| 6 | Đăng nhập bằng một Hộ kinh doanh khác, gõ URL `/vendor/registrations/<id của hồ sơ trên>` | "Bạn không có quyền thực hiện thao tác này." |

---

## 5. Phường xét duyệt hồ sơ (REG-06)

Đăng nhập bằng tài khoản **cán bộ phường** (xem
[database.md](../database.md#demo-data)) — mỗi cán bộ chỉ thấy hồ sơ thuộc
phường mình:

| SĐT | Phường |
|---|---|
| 0983000001 | Phường Hải Châu 1 (ward 10) |
| 0983000002 | Phường Thanh Khê Đông (ward 11) |
| 0983000003 | Phường An Hải Bắc (ward 12) |

Vào tab **Hộp duyệt** (`/ward/inbox`) → tab **Hồ sơ đăng ký**, mở một hồ sơ rồi
bấm một trong các nút: **Nhận xét duyệt** (chuyển sang ĐANG XÉT DUYỆT),
**Phê duyệt**, **Từ chối**, **Yêu cầu bổ sung**. Mọi quyết định đều bắt buộc
nhập lý do.

> Chỉ cần đăng nhập bằng tài khoản cán bộ phường là dùng được ngay. Trước đây màn
> này bắt dán access token vào một form riêng (màn "Kết nối cán bộ phường"); form
> đó đã bị bỏ — hàng đợi hồ sơ nay dùng chung phiên đăng nhập của ứng dụng.
> Nếu tài khoản chưa được gán phường, màn hình báo "Tài khoản chưa được gán
> phường" thay vì lỗi chung chung.

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | Mở tab **Hồ sơ đăng ký** | Chỉ thấy hồ sơ của phường mình; hồ sơ **Ưu tiên** (fast-track) nằm trên cùng |
| 2 | Mở một hồ sơ thiếu giấy tờ bắt buộc | Mục **Điều kiện cần xử lý** báo thiếu giấy tờ; **không** có nút Phê duyệt, vẫn có Yêu cầu bổ sung / Từ chối (BR-07) |
| 3 | Mở hồ sơ đủ giấy tờ | Mục **Giấy tờ minh chứng** hiện đúng ảnh vendor đã nộp; có đủ 4 nút |
| 4 | Bấm **Yêu cầu bổ sung**, nhập lý do | Trạng thái → CHỜ BỔ SUNG GIẤY TỜ; vendor thấy lý do ở màn chi tiết hồ sơ và nhận thông báo |
| 5 | Mở cùng hồ sơ ở 2 tab, quyết định ở tab 1 rồi quyết định ở tab 2 | Tab 2 báo lỗi "Hồ sơ đã thay đổi…" (409), không ghi đè |
| 6 | Đăng nhập cán bộ phường khác, mở URL hồ sơ vừa xem | Không tìm thấy (404) |

Kiểm tra phía vendor (F5 trang chi tiết sau mỗi lần xét duyệt):

| Trạng thái | Kết quả mong đợi |
|---|---|
| `MORE_INFORMATION_REQUIRED` | Nhãn **CẦN BỔ SUNG**, thẻ đỏ **Phản hồi từ Phường** hiện lý do; có **Chỉnh sửa** và **Rút hồ sơ**. Sửa + tải thêm ảnh → gửi lại → nhãn về **ĐÃ NỘP**, ảnh mới xuất hiện trong mục giấy tờ |
| `MORE_INFORMATION_REQUIRED` khi đang có **hồ sơ khác** ở trạng thái SUBMITTED | Gửi lại bị chặn: "Bạn đang có một hồ sơ chờ xét duyệt…" (BR-09) |
| `UNDER_REVIEW` | Nhãn **ĐANG XÉT**; không có Chỉnh sửa, vẫn có **Rút hồ sơ** |
| `APPROVED` (Cửa hàng cố định) | Nhãn **ĐÃ DUYỆT**, ngày xét duyệt; không có Chỉnh sửa, vẫn có **Rút hồ sơ** (BE chặn nếu đã có hợp đồng thuê, BR-16). Mục **Tiếp theo** (thuê ô liền kề / cập nhật địa chỉ) **bị ẩn khi chạy với Backend** — hai màn đó chưa có API client, nên ẩn thay vì dẫn tới ngõ cụt |
| `REJECTED` | Nhãn **TỪ CHỐI**, hiện lý do; không có nút thao tác |

---

## 6. Test API không qua giao diện

- **File `.http`:** mở `src/StreetBiz.API/StreetBiz.API.http` (VS Code cần
  extension *REST Client*; Visual Studio 2022 hỗ trợ sẵn). Bấm **Send Request**
  lần lượt từ 0 → 3, dán OTP từ terminal vào biến `@otp`. Các request phía sau
  tự lấy token từ request đăng nhập.
- **Swagger:** http://localhost:5023/swagger → gọi `POST /api/auth/login`, copy
  `accessToken`, bấm nút **Authorize** ở đầu trang, dán token (không cần gõ
  `Bearer`), rồi gọi các endpoint cần đăng nhập. Endpoint upload có nút chọn file.

Bảng mã lỗi mà FE dựa vào:

| HTTP | `type` | Khi nào |
|---|---|---|
| 400 | `validation_error` | Sai dữ liệu; `errors` là object theo tên trường (`PhoneNumber`, `CurrentPassword`, `WardUnitId`, `File`…) |
| 401 | *(body rỗng)* | Token thiếu / hết hạn / **phiên đã bị thu hồi** → FE tự refresh rồi thử lại |
| 401 | `unauthorized` | Sai mật khẩu đăng nhập, OTP sai / hết hạn / bị khoá |
| 403 | `forbidden` | Không phải chủ hồ sơ / file, không phải vai trò Hộ kinh doanh |
| 409 | `conflict` | Số đã đăng ký, BR-09 |
| 422 | `domain_rule` | Hồ sơ không còn sửa được, đăng xuất phiên đang dùng |
| 429 | `otp_cooldown` | Gửi lại OTP trong 60s; có `retryAfterSeconds` và header `Retry-After` |

---

## 7. Xử lý sự cố

| Hiện tượng | Nguyên nhân / cách xử lý |
|---|---|
| `Failed to bind to address http://127.0.0.1:5023: address already in use` | Một API khác đang chạy. PowerShell: `Get-NetTCPConnection -LocalPort 5023 -State Listen` để lấy `OwningProcess`, rồi `Stop-Process -Id <id>` |
| FE báo "Không kết nối được máy chủ", Console có lỗi **CORS** | API chưa chạy, hoặc chạy bản cũ. Ở Development, BE chấp nhận mọi cổng `localhost`; môi trường khác phải thêm địa chỉ FE vào `Cors:AllowedOrigins` |
| Ô chọn phường trống / báo lỗi | Chưa seed phường → chạy lại `scripts/setup-local-db.ps1` |
| Lỗi 500 khi đăng ký | Thiếu bảng `Roles` / DB trống → chạy `scripts/setup-local-db.ps1` |
| Không thấy OTP | Xem đúng terminal đang chạy API; tìm `[DEV-SMS]`. Script e2e cần API chạy kèm `| tee api.log` trong Git Bash (PowerShell `Tee-Object` ghi UTF-16, grep không đọc được) |
| Đang đăng nhập nhưng mọi màn báo lỗi / bị đá ra | Token cũ từ phiên trước hoặc từ chế độ mock. Đăng xuất, hoặc xoá `streetbiz-auth` và `streetbiz-tokens` trong Local Storage |
| Script e2e: `upload jpg ... got=[000]` | curl không đọc được file; chạy script trong Git Bash, không chạy trong PowerShell |
| `dotnet ef` lỗi file bị khoá khi chạy `setup-local-db.ps1` | Tắt API rồi chạy lại |

---

## 8. Giới hạn hiện tại (không phải lỗi)

- **SMS:** OTP chỉ in ra console (`LoggingSmsSender`), chưa nối nhà cung cấp SMS.
- **Lưu file:** giấy tờ lưu trên ổ đĩa (`App_Data/uploads`), phù hợp chạy một
  server. Khi triển khai nhiều server cần đổi `IFileStorage` sang blob storage.
- **OCR / AI kiểm tra giấy tờ:** giao diện có gợi ý, nhưng BE chưa xử lý
  (`ocrExtractedData` là dữ liệu client gửi lên, không được kiểm chứng).
- **Xoá giấy tờ hết hạn lưu trữ:** mỗi giấy tờ đã có `retention_expires_at`
  (2 năm, BR-47) nhưng chưa có job nào dọn file quá hạn.
- **Giới hạn tần suất:** ở Development được nới rộng để không cản trở demo và
  script e2e; số liệu thật chỉ áp dụng ngoài Development.
- Các module khác của FE (thuê ô, phí, cửa hàng…) vẫn chạy bằng dữ liệu mock,
  kể cả khi `VITE_USE_MOCK_API=false`.
