# Hướng dẫn kiểm thử: Sidewalk Slot & Rental

Tài liệu này hướng dẫn chạy và kiểm thử module **Sidewalk Slot & Rental**
(SIDE-01…SIDE-13) trên StreetBiz-BE, ở máy local. Đợt này **chỉ có backend** —
FE/app vẫn chạy bằng mock, nên kiểm thử ở đây là qua file `.http` / Swagger,
không có kịch bản trên giao diện như tài liệu Auth/Onboarding.

Đọc trước [auth-vendor-onboarding.md](auth-vendor-onboarding.md)
mục 1–2 để có database và biết cách chạy API — tài liệu này không lặp lại phần
đó, chỉ nói thêm phần riêng của SIDE.

---

## 1. Chuẩn bị riêng cho SIDE

Ngoài phần chuẩn bị chung (mục 1 của tài liệu Auth), module SIDE cần **một hồ
sơ APPROVED và một hợp đồng ACTIVE**.

### 1.1 Dùng dữ liệu demo (khuyến nghị)

Seed demo (`db/StreetBiz_Demo_Seed.sql`, nạp bởi `scripts/setup-local-db.ps1 -Recreate`)
đã dựng sẵn mọi thứ, không cần chạy thêm script nào:

- Hồ sơ APPROVED: 1 (Bánh mì & Xôi Cô Lan) và 9 (Bún chả Hải Châu), cùng thuộc hộ `0905000101`.
- Hợp đồng ACTIVE: 1 (ô NVL-01) và 2 (ô NVL-08), mỗi hợp đồng có giấy phép số và lịch phí.
- Một đơn thuê PENDING (đơn 3, ô 3) để Phường duyệt thử qua WARD-07/08.

Đăng nhập bằng `0905000101` / `Password123!`; danh sách tài khoản đầy đủ ở
[database.md](../database.md#demo-data).

### 1.2 Hoặc duyệt thủ công bằng SQL (giống mục 5 tài liệu Auth)

Nếu muốn kiểm soát chính xác hồ sơ/vendor nào được dùng để test:

```sql
UPDATE BusinessRegistrations SET registration_status = 'APPROVED', reviewed_at = SYSUTCDATETIME()
WHERE registration_id = 1;   -- đổi id
```

Sau đó nộp đơn thuê ô qua `POST /api/vendor/rental-applications/open-slot`
(SIDE-03B), rồi để Phường duyệt (`POST /api/ward/rental-applications/{id}/decision`), hoặc tự duyệt đơn + tạo hợp đồng bằng SQL:

```sql
UPDATE RentalApplications SET application_status = 'APPROVED' WHERE application_id = 1;

INSERT INTO RentalContracts (application_id, slot_id, vendor_id, start_date, end_date, contract_status)
VALUES (1, 1, 1, CAST(SYSUTCDATETIME() AS DATE), DATEADD(DAY, 90, CAST(SYSUTCDATETIME() AS DATE)), 'ACTIVE');

UPDATE SidewalkSlots SET slot_status = 'ACTIVE' WHERE slot_id = 1;
```

---

## 2. Chạy hệ thống

Giống tài liệu Auth mục 2: `dotnet run --project src/StreetBiz.API`, mở
Swagger ở `http://localhost:5023/swagger`. Module SIDE chưa có FE nên không
cần chạy `StreetBiz-FE`.

---

## 3. Kiểm thử tự động

```powershell
cd StreetBiz-BE
dotnet test StreetBiz.Backend.sln
```

Kỳ vọng: tất cả `Passed!`. Các test đáng chú ý trong
`StreetBiz.Application.Tests`:

- **SIDE-01**: ô nằm trong bbox nhưng ngoài bán kính bị loại khỏi kết quả tìm kiếm.
- **SIDE-03A**: vendor Bán hàng lưu động gọi `/adjacent` → 422; ô cách quá bán
  kính BR-11 → 422; đã có hợp đồng adjacent ACTIVE (BR-12) → 409.
- **SIDE-06/07**: có renewal đang mở → 409; hợp đồng còn nợ → 422 trước khi
  chạm tới trigger DB.
- **SIDE-08**: token round-trip `Create` → `TryParse`; token bị sửa 1 ký tự bị
  từ chối.
- **SIDE-11**: thiếu `proposalPhotoUrl` → 400 (validator, không phải 500 từ
  CHECK constraint); trùng `slot_code` → tự thử lại rồi mới 409.
- **SIDE-09/10**: vendor Bán hàng lưu động không được đổi địa chỉ (422); có
  yêu cầu đang mở (409); trả ô không phải của mình (403).
- **SIDE-12/13**: tự chuyển cho mình (400); người nhận chưa có hồ sơ APPROVED
  (422, BR-26); hợp đồng còn nợ (422, BR-27); người ngoài accept (403); accept
  hai lần (409).

`StreetBiz.Infrastructure.Tests` kiểm tra `SqlErrorTranslator` dịch đúng lỗi
trigger 50000 thành exception có nghĩa, và `GeoMathTests` kiểm khoảng cách
Haversine giữa các toạ độ Đà Nẵng đã biết trước.

---

## 4. Kiểm thử qua `.http` / Swagger

File `.http`: `src/StreetBiz.API/StreetBiz.API.http`, phần **Sidewalk Slot &
Rental** ở cuối file. Đăng nhập trước (request AUTH-03) để có `@token`, rồi
chạy tuần tự các request SIDE — mỗi request đã có sẵn số liệu mẫu, sửa
`@slotId`/`@contractId`/`@registrationId` theo dữ liệu thật trên máy bạn.

Swagger: gọi `POST /api/auth/login`, copy `accessToken`, bấm **Authorize**
(dán token, không cần gõ `Bearer`).

### 4.1 Tìm & xem ô (SIDE-01, SIDE-02)

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | `GET /api/sidewalk-slots?lat=16.0120&lng=108.2400&radiusMeters=500` | Danh sách ô, mỗi ô có `distanceMeters`, sắp xếp tăng dần |
| 2 | Gọi lại với `radiusMeters=10` | Danh sách rỗng hoặc chỉ còn ô rất gần |
| 3 | `GET /api/sidewalk-slots?minLat=...&maxLat=...&minLng=...&maxLng=...` thiếu 1 cạnh | 400, thiếu tham số |
| 4 | `GET /api/sidewalk-slots/{slotId}` với id không tồn tại | 404 |

### 4.2 Nộp đơn thuê (SIDE-03A, SIDE-03B)

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | `POST /rental-applications/open-slot` với ô đang `AVAILABLE` và `commitmentsAccepted: true` | 200, đơn `PENDING`/method `MANUAL_SELECTED`, `commitments_accepted_at` được lưu |
| 1b | Như trên nhưng `commitmentsAccepted: false` hoặc thiếu | 400 (phải chấp nhận cam kết) |
| 2 | Gọi lại cho **cùng ô** khi đơn trên chưa được xét | 409 (đã có đơn đang mở cho ô) |
| 3 | `POST /rental-applications/adjacent` bằng hồ sơ **Bán hàng lưu động** | 422 (chỉ Cửa hàng cố định) |
| 4 | `POST /rental-applications/adjacent` với ô cách địa chỉ đăng ký > 150m | 422 (BR-11, ngoài bán kính) |
| 5 | Hồ sơ Cửa hàng cố định đã có 1 hợp đồng adjacent ACTIVE, nộp thêm | 409 (BR-12) |

### 4.3 Theo dõi đơn & hợp đồng (SIDE-04, SIDE-05)

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | `GET /rental-applications` | Chỉ thấy đơn của chính vendor đang đăng nhập |
| 2 | `GET /rental-applications/{id}` với đơn của **vendor khác** | 403 (không phải 404) |
| 3 | `POST /rental-applications/{id}/withdraw` khi đơn đã `APPROVED`/`REJECTED` | 422, không rút được |
| 4 | `GET /rental-contracts?status=ACTIVE` | Chỉ hợp đồng ACTIVE của vendor |

### 4.4 Gia hạn & trả ô (SIDE-06, SIDE-07)

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | `POST /rental-contracts/{id}/renewals` | 200, `renewalStatus = PENDING` |
| 2 | Gọi lại ngay khi renewal trên chưa được xét | 409 |
| 3 | `POST /rental-contracts/{id}/cancel` với hợp đồng bình thường | 200, hợp đồng `CANCELLED`, ô về lại `AVAILABLE` |
| 4 | **Test quan trọng nhất mục này** — giả lập nợ rồi huỷ: | |

```sql
INSERT INTO FeeSchedules (contract_id, revision, total_amount) VALUES (2, 1, 35000);
DECLARE @fsid BIGINT = SCOPE_IDENTITY();
INSERT INTO FeeScheduleItems (fee_schedule_id, due_date, amount, item_status) VALUES (@fsid, '2026-01-01', 35000, 'OVERDUE');
```

Gọi lại `POST /rental-contracts/2/cancel` → phải nhận **422** với thông điệp
"Cannot return a slot while fees are overdue…", **không phải 500**. Đây là
bằng chứng cho cả pre-check ở handler lẫn `SqlErrorTranslator` (nếu race qua
được pre-check, trigger DB vẫn chặn và được dịch đúng nghĩa).

### 4.5 Giấy phép số (SIDE-08)

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | `GET /rental-contracts/{id}/permit` | 200, có `token`, `effectiveStatus = VALID` |
| 2 | Đổi `RentalContracts.contract_status = 'SUSPENDED'` bằng SQL rồi gọi lại | `effectiveStatus` đổi theo dù `DigitalPermits.permit_status` vẫn `ACTIVE` — chứng minh giá trị luôn đọc từ view `vw_PermitValidity`, không tự suy từ `permit_status` |
| 3 | Hợp đồng chưa từng có giấy phép | 404 |

### 4.6 Đề xuất ô mới (SIDE-11)

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | `POST /slot-proposals` thiếu `proposalPhotoUrl` | 400 (validator) |
| 2 | Đủ dữ liệu, `zoneId` hợp lệ | 200, `sourceType = VENDOR_PROPOSED`, `proposalReviewStatus = PENDING` |
| 3 | `zoneId` không tồn tại | 400, `ZoneId` |
| 4 | `GET /slot-proposals` | Danh sách đề xuất của chính vendor |

### 4.7 Đổi địa chỉ (SIDE-09, SIDE-10)

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | `POST /address-changes` bằng hồ sơ **Bán hàng lưu động** | 422 |
| 2 | Bằng hồ sơ Cửa hàng cố định, không kèm toạ độ | 200, `newLatitude/newLongitude` được điền tự động qua geocoding (nếu Nominatim trả kết quả) hoặc `null` (nếu không, **vẫn** tạo được yêu cầu) |
| 3 | Gọi lại khi yêu cầu trên chưa được xét | 409 |
| 4 | `releasedContractId` là hợp đồng của **vendor khác** | 403 |

### 4.8 Chuyển nhượng ô (SIDE-12, SIDE-13)

Cần 2 tài khoản vendor đã đăng nhập (bên gửi và bên nhận — xem 2 request
`login`/`loginReceiver` trong file `.http`).

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | `POST /slot-transfers` với `toVendorPhone` là **số của chính mình** | 400 |
| 2 | `toVendorPhone` của vendor chưa có hồ sơ APPROVED | 422 (BR-26) |
| 3 | Hợp đồng đang nợ (giả lập như mục 4.4.4) | 422 (BR-27) |
| 4 | Đủ điều kiện | 200, `transferStatus = PENDING` |
| 5 | Gọi lại `POST /slot-transfers` cho **cùng hợp đồng** | 409 (đã có transfer đang mở) |
| 6 | Bên gửi tự `accept` transfer của chính mình | 403 |
| 7 | Bên nhận `GET /slot-transfers?direction=incoming` | Thấy transfer vừa nhận |
| 8 | Bên nhận `POST /slot-transfers/{id}/accept` | 200, `transferStatus = ACCEPTED_BY_RECEIVER` |
| 9 | Gọi lại `accept` lần hai | 409 (không còn PENDING) |
| 10 | `RentalContracts.vendor_id` sau khi accept | **Không đổi** — chỉ WARD-18 (chưa có) mới đổi chủ |

### 4.9 Màn "Ô thuê": thông tin tuyến, báo giá, giữ chỗ

Cần database dựng từ schema + seed mới nhất (`scripts/setup-local-db.ps1 -Recreate`).
Tuyến Nguyễn Văn Linh là `zoneId = 1` (xem bảng `PricingZones`).

| # | Thao tác | Kết quả mong đợi |
|---|---|---|
| 1 | `GET /api/sidewalk-zones/{zoneId}` | Tên/mã/quyết định/đoạn tuyến, hạn nộp, liên hệ Phường, 3 khoản phí, 6 hạng mục (`features`) |
| 2 | `GET /api/sidewalk-zones/99999` | 404 |
| 3 | `GET /api/sidewalk-slots/{slotId}` | Có `hasPower/hasWater/hasTrashBin/businessCategory`; `tenantName` chỉ có ở ô có hợp đồng ACTIVE |
| 4 | `GET /api/sidewalk-slots/{slotId}/quote?termDays=90` | Dòng `RENT` + các dòng `FEE`, `total` = giá/ngày × 90 + phí `PER_DAY` × 90 + phí `PER_TERM` |
| 5 | `quote?termDays=0` hoặc `366` | 400 |
| 6 | `POST /vendor/slot-holds` `{registrationId, slotId}` với ô `AVAILABLE` | 200, `expiresAt` = bây giờ + 15 phút (UTC, có `Z`) |
| 7 | Giữ lại đúng ô đó | 200, `expiresAt` được gia hạn |
| 8 | Giữ tới ô thứ 4 cùng lúc | 409 (tối đa 3) |
| 9 | Ô đang `ACTIVE`/`SUSPENDED`, hoặc đã có đơn mở | 409 |
| 10 | `registrationId` của người khác | 403 |
| 11 | `GET /vendor/slot-holds?registrationId=` | Chỉ các hold còn hạn của hồ sơ đó |
| 12 | `DELETE /vendor/slot-holds/{slotId}?registrationId=` hai lần | 200 rồi 404 |
| 13 | Hồ sơ B giữ ô, hồ sơ A giữ/nộp đơn cho **cùng ô** | 409 cả hai (`held by another vendor`) |
| 14 | Chỉnh `SlotHolds.expires_at` về quá khứ bằng SQL rồi gọi lại | Hold coi như không còn: ô hết `holdExpiresAt`, A giữ/nộp đơn được |
| 15 | Nộp đơn `open-slot` cho ô mình đang giữ | 200 và hold của mình biến mất |

---

## 5. Bảng mã lỗi

Giống tài liệu Auth (`400 validation_error`, `403 forbidden`, `404 not_found`,
`409 conflict`, `422 domain_rule`). Không có mã lỗi mới riêng cho SIDE.

---

## 6. Xử lý sự cố

| Hiện tượng | Nguyên nhân / cách xử lý |
|---|---|
| Mọi request SIDE trả 401 dù đã đăng nhập | Token hết hạn (mặc định 60 phút) — đăng nhập lại |
| `POST rental-applications/adjacent` luôn 422 dù ô rất gần | Hồ sơ thiếu `addressLatitude/addressLongitude` — BR-11 không tính được khoảng cách nếu địa chỉ chưa có toạ độ |
| `GET permit` trả 404 dù hợp đồng ACTIVE | Hợp đồng seed bằng SQL thủ công (mục 1.2) chưa có dòng `DigitalPermits` — thêm thủ công (seed demo đã có sẵn) |
| `SqlErrorTranslator` không bắt được lỗi trigger, vẫn thấy 500 | Kiểm tra thông điệp trigger trong `db/StreetBiz_SQL_Server.sql` có đổi chữ không — translator so khớp theo chuỗi literal |

---

## 7. Giới hạn hiện tại (không phải lỗi)

- **Phường xét duyệt:** WARD-07/08/16/17/18 đã có API (xem
  [ward-slot-workflows.md](../ward-slot-workflows.md)); WARD-09 (duyệt gia hạn)
  chưa có. Trạng thái "đã duyệt" trong SIDE có sẵn từ seed demo, hoặc giả lập
  bằng SQL như mục 1.
- **FE/app:** chưa nối API thật cho nhóm SIDE, vẫn chạy bằng mock.
- **AIC-04/06/07** (đề xuất giá, phát hiện lệch geofence, đánh giá đề xuất ô):
  phụ thuộc dữ liệu từ WARD-11/WARD-02/WARD-16 nên chưa làm được ở đợt này.
