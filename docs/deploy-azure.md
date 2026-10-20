# Deploy API lên Azure App Service

Workflow `.github/workflows/deploy-azure.yml` làm hai việc khi có push lên `main` (hoặc khi bấm *Run workflow*):

1. Chạy test.
2. Build image từ `Dockerfile`, đẩy lên `ghcr.io/<owner>/streetbiz-api:<sha>`, rồi trỏ Web App sang image đó.

## 1. Cấu hình trên GitHub

Vào *Settings → Secrets and variables → Actions*:

| Loại | Tên | Giá trị |
|---|---|---|
| Variable | `AZURE_WEBAPP_NAME` | Tên Web App (phần trước `.azurewebsites.net`) |
| Secret | `AZURE_WEBAPP_PUBLISH_PROFILE` | Nội dung file *Download publish profile* của Web App |

Nếu thiếu một trong hai, workflow dừng ngay ở bước đầu (*Check deploy settings*).

Image trên ghcr.io mặc định là private. Hãy chọn một trong hai cách:
- Đổi package sang public.
- Hoặc vào Web App → *Deployment Center*, khai báo registry `ghcr.io` với username GitHub và một PAT có quyền `read:packages`.

## 2. Cấu hình Web App

Tạo Web App: *Linux*, *Container*.

Trong *Configuration → General settings*:
- **Web sockets: On.** SignalR (đơn hàng, chat, chatbot) cần web socket.
- **ARR affinity: On** nếu chạy nhiều hơn 1 instance.

Các tác vụ nền (nhắc phí, hết hạn giữ ô) chạy trên mọi instance, nên tốt nhất giữ **1 instance**.

## 3. App Settings (biến môi trường)

Tên có `__` tương ứng với dấu `:` trong `appsettings.json`.

### Bắt buộc: thiếu thì app không khởi động

| Tên | Ghi chú |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `WEBSITES_PORT` | `8080` (cổng image lắng nghe) |
| `STREETBIZ_DB_CONNECTION` | Connection string Azure SQL. Không dùng tài khoản `sa`. |
| `Jwt__SigningKey` | Chuỗi ngẫu nhiên ≥ 32 ký tự |
| `Permit__SigningKey` | ≥ 32 ký tự. Ký mã QR giấy phép; khoá rỗng sẽ cho phép làm giả. |
| `OrderPickup__SigningKey` | ≥ 32 ký tự. Ký mã nhận hàng. |
| `Otp__HashKey` | ≥ 32 ký tự |

Lưu ý về khoá:
- Tạo khoá mới bằng `openssl rand -base64 48`.
- **Không dùng lại** các khoá `dev-…` từng nằm trong lịch sử git.
- Đổi `Jwt__SigningKey` sẽ đăng xuất mọi người.
- Đổi `Permit__SigningKey` làm mã QR của các giấy phép đã cấp không còn quét được.

### Bắt buộc để chức năng chạy được

| Tên | Thiếu thì sao |
|---|---|
| `Cors__AllowedOrigins__0` | Domain FE, ví dụ `https://streetbiz.vercel.app`, không có `/` ở cuối. Thiếu thì FE trên Vercel bị CORS chặn hết request. Thêm `__1`, `__2` cho domain preview hoặc domain riêng. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true`. Thiếu thì app thấy IP của proxy Azure thay cho IP người dùng, nên giới hạn đăng nhập (10 lần / 5 phút / IP) bị gộp làm một cho **toàn bộ** người dùng. |
| `Storage__Provider` | `AzureBlob`. Ổ đĩa của container bị xoá mỗi lần restart hoặc deploy, nên ảnh và giấy tờ upload sẽ mất. |
| `Storage__AzureBlob__ConnectionString` | Connection string Storage account (hoặc dùng `Storage__AzureBlob__AccountUrl` kèm managed identity) |
| `Sms__Provider`, `Sms__Endpoint`, `Sms__ApiKey` | `Http` và endpoint của nhà cung cấp SMS. Thiếu thì OTP (đăng ký, quên mật khẩu) trả 500. |

### Tuỳ chọn

| Tên | Khi nào cần |
|---|---|
| `Payments__Momo__*` (`ApiBaseUrl`, `PartnerCode`, `AccessKey`, `SecretKey`, `ReturnUrl`, `FeeReturnUrl`, `PenaltyReturnUrl`, `NotifyUrl`) | Thanh toán MoMo. Thiếu thì màn thanh toán báo "chưa sẵn sàng". `ReturnUrl` trỏ về domain FE; `NotifyUrl` = `https://<web-app>.azurewebsites.net/api/payments/momo/callback`. |
| `AiCompliance__FptAi__ApiKey`, `AiCompliance__Gemini__ApiKeys__0`, `AiCompliance__Groq__ApiKeys__0` | eKYC, kiểm tra hồ sơ bằng AI, chatbot |
| `Geolocation__Boundaries__…` | Ranh giới phường đã xác minh. Thiếu thì kiểm tra geofence (SYS-02) trả 503. |
| `Nominatim__UserAgent` | Nên đặt tên kèm email liên hệ theo chính sách của Nominatim |

Giữ nguyên `Payments__SandboxEnabled` là `false` trên production.

## 4. Database

1. Tạo Azure SQL Database.
2. Chạy `db/StreetBiz_SQL_Server.sql`, sau đó (nếu cần dữ liệu demo) chạy `db/StreetBiz_Demo_Seed.sql` bằng sqlcmd hoặc Azure Data Studio. Với sqlcmd phải thêm `-I` (bật `QUOTED_IDENTIFIER`), vì script có filtered index.
3. Mở firewall của SQL server cho Azure services.

Dữ liệu demo có tài khoản quản trị với mật khẩu công khai `Password123!`. Hãy đổi mật khẩu (hoặc xoá tài khoản demo) trước khi cho người ngoài dùng.

## 5. Kiểm tra sau deploy

- `GET https://<web-app>.azurewebsites.net/health` trả `Healthy`.
- Swagger tắt trên production (`/swagger` trả 404), đó là bình thường.
- Đăng nhập từ FE trên Vercel không gặp lỗi CORS trong console.
