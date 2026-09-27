# Thiết lập thanh toán MoMo thật (local development)

`ConfiguredPaymentGateway` chỉ gọi API thật của MoMo khi **cả 4** khoá cấu hình
`Payments:Momo:PartnerCode`, `AccessKey`, `SecretKey`, `ApiEndpoint` đều được
điền — thiếu bất kỳ khoá nào, hệ thống tự động quay về đường dẫn giả lập
(`streetbiz://...`) như trước, không có gì bị hỏng nếu bạn chưa làm bước này.

## 1. Lấy khoá — cách nhanh cho đồ án, không cần đăng ký

Không cần tài khoản merchant. MoMo công bố sẵn một bộ
`partnerCode`/`accessKey`/`secretKey` **test dùng chung**, đúng mục đích để ai
cũng thử được luồng tích hợp ngay:

```
PartnerCode = MOMO
AccessKey   = F8BBA842ECF85
SecretKey   = K951B6PE1waDMi640xX08PD3vg6EkVlz
ApiEndpoint = https://test-payment.momo.vn/v2/gateway/api/create
```

**Vì đây là key dùng chung, không phải của riêng bạn:**
- Nó có thể bị MoMo thu hồi/đổi bất cứ lúc nào mà không báo trước, vì không
  gắn với tài khoản ai cả. Nếu tự nhiên báo lỗi chữ ký hoặc endpoint không còn
  hoạt động, đó là lý do — kiểm tra lại tại `developers.momo.vn` xem còn hiệu
  lực không.
- Người khác cũng đang dùng chung key này để test, nên nếu muốn chắc chắn ổn
  định cho báo cáo/demo chính thức, làm theo mục dưới đây để có bộ khoá riêng
  (miễn phí, ~5 phút, không cần hồ sơ doanh nghiệp vì chỉ là app test):

  1. Vào **business.momo.vn** (MoMo for Business) → đăng ký tài khoản merchant
     bằng số điện thoại của bạn.
  2. Trong mục **Phát triển / Developer**, chọn tạo một **ứng dụng test
     (sandbox)** — MoMo cấp ngay một bộ `partnerCode`/`accessKey`/`secretKey`
     riêng cho ứng dụng test này, dùng tiền giả.
  3. Đọc tài liệu tại **developers.momo.vn** (mục "Cổng thanh toán MoMo" /
     "AIO — captureWallet") để đối chiếu request/response, chữ ký, và đặc
     biệt là **endpoint tạo thanh toán hiện tại** — họ có thể đổi đường dẫn
     theo thời gian, nên lấy `ApiEndpoint` chính xác từ tài liệu của họ tại
     thời điểm bạn thiết lập.

## 2. Địa chỉ nhận callback (`IpnUrl`) — máy local cần một "đường hầm"

MoMo xác nhận thanh toán bằng cách **server của MoMo tự gọi vào `IpnUrl` của
bạn** (IPN — Instant Payment Notification), không phải trình duyệt của người
dùng gọi. `http://localhost:7147/...` chỉ tồn tại trên máy bạn, MoMo không với
tới được, nên callback sẽ không bao giờ đến nếu để nguyên localhost — bất kể
bạn dùng bộ key nào ở bước 1.

Cách khắc phục khi chạy ở máy phát triển: dùng một dịch vụ "đường hầm" (tunnel)
để có địa chỉ HTTPS công khai trỏ về máy bạn, ví dụ `ngrok`:

```powershell
ngrok http https://localhost:7147
```

Lấy URL `https://xxxx.ngrok-free.app` mà `ngrok` in ra, dùng nó làm `IpnUrl`:
`https://xxxx.ngrok-free.app/api/payments/momo/callback`. URL này đổi mỗi lần
chạy lại `ngrok` (bản miễn phí), nên nếu callback không tới nữa, kiểm tra lại
`IpnUrl` đã cập nhật theo URL mới chưa.

`RedirectUrl` (nơi MoMo đưa trình duyệt người dùng quay lại sau khi thanh
toán) không cần đường hầm — dùng thẳng `http://localhost:5173/vendor/finance`.

## 3. Cấu hình bằng user-secrets (không commit vào appsettings.json)

```powershell
dotnet user-secrets --project src/StreetBiz.API set "Payments:Momo:PartnerCode" "MOMO"
dotnet user-secrets --project src/StreetBiz.API set "Payments:Momo:AccessKey" "F8BBA842ECF85"
dotnet user-secrets --project src/StreetBiz.API set "Payments:Momo:SecretKey" "K951B6PE1waDMi640xX08PD3vg6EkVlz"
dotnet user-secrets --project src/StreetBiz.API set "Payments:Momo:ApiEndpoint" "https://test-payment.momo.vn/v2/gateway/api/create"
dotnet user-secrets --project src/StreetBiz.API set "Payments:Momo:RedirectUrl" "http://localhost:5173/vendor/finance"
dotnet user-secrets --project src/StreetBiz.API set "Payments:Momo:IpnUrl" "https://xxxx.ngrok-free.app/api/payments/momo/callback"
```

(Nếu bạn tự đăng ký được key riêng ở bước 1, thay 3 dòng `PartnerCode`/
`AccessKey`/`SecretKey` bằng giá trị của bạn — mọi thứ khác giữ nguyên.)

Kiểm tra đã set đúng:

```powershell
dotnet user-secrets --project src/StreetBiz.API list
```

## 4. Kiểm tra tắt bước xác nhận giả lập

Một khi 4 khoá trên đã điền đủ, endpoint xác nhận giả lập
(`POST /api/vendor/finance/payments/{id}/sandbox-confirm`) sẽ **từ chối**
mọi giao dịch MoMo (báo lỗi 422 "Cổng thanh toán này đã được cấu hình để
thanh toán thật") — đây là chủ đích, để không ai vô tình đánh dấu một khoản
tiền MoMo thật là "đã thu" mà không hề có tiền thật vào. Giao diện vendor cũng
tự động không gọi endpoint này nữa cho MoMo một khi có cấu hình thật (xem
`docs/fee-payment-invoicing.md`).

## 5. Test đầu-cuối

1. Chạy `ngrok`, cập nhật `IpnUrl` theo URL mới nếu cần, chạy backend + frontend
   như bình thường.
2. Đăng nhập vendor, vào `/vendor/finance`, bấm trả một kỳ phí đang PENDING,
   chọn MoMo.
3. Trình duyệt phải chuyển hẳn sang một trang **thật của MoMo**
   (`test-payment.momo.vn/...`), không phải trang trong ứng dụng StreetBiz.
4. Dùng app MoMo (hoặc tài khoản test MoMo cấp kèm bộ khoá sandbox) quét
   mã/xác nhận thanh toán trên trang đó.
5. MoMo tự gọi vào `IpnUrl` của bạn trong vài giây; kiểm tra log backend thấy
   `PaymentCallbackEvents`/hoá đơn mới được tạo, hoặc mở lại `/vendor/finance`
   thấy kỳ phí đã chuyển PAID.
6. Nếu bước 5 không xảy ra: kiểm tra `ngrok` có còn chạy không, `IpnUrl` có
   khớp URL `ngrok` hiện tại không, và log backend có ghi nhận request POST
   vào `/api/payments/momo/callback` không (nếu có request tới nhưng
   `signatureValid=false`, khả năng cao `SecretKey` sai hoặc endpoint IPN bạn
   cấu hình khác với endpoint MoMo thực sự gọi).
