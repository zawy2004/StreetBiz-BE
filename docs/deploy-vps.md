# Deploy API và database lên VPS

Frontend vẫn chạy trên **Vercel** (không đổi). Backend và SQL Server chạy trên **một VPS** bằng Docker
Compose, thay cho Azure App Service + Azure SQL trước đây.

```
Trình duyệt ──https──> Vercel (FE, tĩnh)
     │
     └──https/wss──> VPS: Caddy :443 ──> api :8080 ──> sqlserver :1433 (mạng nội bộ, không mở ra ngoài)
                          (TLS tự động)    (image GHCR)   (SQL Server 2022 Express)
                                           volume uploads   volume sqlserver-data
```

Mọi file chạy trên server nằm ở [`deploy/vps/`](../deploy/vps/):

| File | Vai trò |
|---|---|
| `docker-compose.prod.yml` | 3 container: `caddy` (HTTPS), `api`, `sqlserver`. Chỉ `caddy` mở cổng 80/443. |
| `Caddyfile` | Reverse proxy tới API, tự xin và gia hạn chứng chỉ Let's Encrypt, hỗ trợ WebSocket (SignalR). |
| `.env.example` | Biến cho docker compose: domain, image tag, mật khẩu SQL. Copy thành `.env`. |
| `api.env.example` | Cấu hình của API: khoá ký, CORS, SMS, thanh toán, AI. Copy thành `api.env`. |
| `init-db.sh` | Tạo `StreetBizDB` lần đầu từ `db/*.sql` và tài khoản SQL riêng cho app. |
| `deploy.sh` | Kéo image theo tag, khởi động lại, kiểm tra `/health`; lỗi thì tự quay về bản trước. |
| `backup.sh` | Sao lưu database và file upload, giữ `BACKUP_KEEP_DAYS` ngày. |

Workflow [`.github/workflows/deploy-vps.yml`](../.github/workflows/deploy-vps.yml) chạy khi push lên
`main`: test → build image `ghcr.io/<owner>/streetbiz-api:<sha>` → copy `deploy/vps` + `db/*.sql` lên
server → chạy `deploy.sh <sha>` qua SSH.

## 1. Chuẩn bị VPS và tên miền

- **VPS:** Ubuntu 22.04 hoặc 24.04, **tối thiểu 2 vCPU, 4 GB RAM** (SQL Server cần ít nhất 2 GB), 40 GB ổ đĩa.
- **Tên miền cho API**, ví dụ `api.streetbiz.vn`: tạo bản ghi **A** trỏ về IP của VPS *trước* lần deploy
  đầu, để Caddy xin được chứng chỉ HTTPS.

Cài đặt lần đầu trên server (đăng nhập bằng user có sudo):

```bash
# Docker Engine + Compose plugin (kho chính thức của Docker)
sudo apt-get update && sudo apt-get install -y ca-certificates curl
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
  | sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
sudo apt-get update && sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin

# User riêng cho deploy, đăng nhập bằng SSH key, được dùng docker
sudo adduser --disabled-password --gecos "" deploy
sudo usermod -aG docker deploy
sudo mkdir -p /opt/streetbiz/backups /opt/streetbiz/db
sudo chown -R deploy:deploy /opt/streetbiz
sudo chown 10001:10001 /opt/streetbiz/backups   # SQL Server trong container chạy với uid 10001

# Tường lửa: chỉ SSH, HTTP, HTTPS. Cổng 1433 KHÔNG mở.
sudo ufw allow OpenSSH && sudo ufw allow 80/tcp && sudo ufw allow 443/tcp && sudo ufw allow 443/udp
sudo ufw enable

# Tự cài bản vá bảo mật của hệ điều hành
sudo apt-get install -y unattended-upgrades
```

Docker có thể bỏ qua luật của `ufw` với các cổng mà container publish. Ở đây chỉ `caddy` publish 80/443, nên
điều này không mở thêm gì; SQL Server không publish cổng nào.

Nên tắt đăng nhập SSH bằng mật khẩu (`PasswordAuthentication no` trong `/etc/ssh/sshd_config`) sau khi
đã thêm key.

## 2. Cấu hình GitHub (repo StreetBiz-BE)

Tạo một cặp SSH key **chỉ dùng để deploy**, trên máy của bạn:

```bash
ssh-keygen -t ed25519 -C "streetbiz-deploy" -f streetbiz_deploy -N ""
ssh-copy-id -i streetbiz_deploy.pub deploy@<IP-VPS>
ssh-keyscan -H <IP-VPS>        # in ra dòng known_hosts để dán vào secret bên dưới
```

Vào *Settings → Secrets and variables → Actions*:

| Loại | Tên | Giá trị |
|---|---|---|
| Secret | `VPS_HOST` | IP hoặc tên miền của VPS |
| Secret | `VPS_USER` | `deploy` |
| Secret | `VPS_SSH_KEY` | Nội dung file **private key** `streetbiz_deploy` |
| Secret | `VPS_KNOWN_HOSTS` | Kết quả của `ssh-keyscan -H <IP-VPS>`. Nhờ đó workflow từ chối kết nối nếu server bị giả mạo. |
| Secret (tuỳ chọn) | `VPS_PORT` | Cổng SSH nếu khác 22 |
| Variable (tuỳ chọn) | `VPS_PATH` | Thư mục trên server, mặc định `/opt/streetbiz` |

Thêm *Environment* tên `production` (*Settings → Environments*). Có thể bật **Required reviewers** để mỗi
lần deploy cần một người bấm duyệt.

Image trên ghcr.io để **private** cũng được: mỗi lần deploy, workflow đăng nhập GHCR trên server bằng token
của chính job đó (hết hạn khi job kết thúc) rồi đăng xuất.

Có thể xoá các secret/variable cũ của Azure: `AZURE_WEBAPP_NAME`, `AZURE_WEBAPP_PUBLISH_PROFILE`.

## 3. Lần đầu: cấu hình và tạo database

Copy thư mục deploy và script database lên server, từ thư mục `StreetBiz-BE` trên máy của bạn:

```bash
scp deploy/vps/* deploy/vps/.env.example deploy@<IP-VPS>:/opt/streetbiz/
scp db/*.sql deploy@<IP-VPS>:/opt/streetbiz/db/
```

Trên server:

```bash
cd /opt/streetbiz
cp .env.example .env && cp api.env.example api.env
chmod 600 .env api.env
nano .env       # API_DOMAIN, SQLSERVER_SA_PASSWORD, DB_APP_PASSWORD
nano api.env    # các khoá ký, CORS, SMS... (xem mục 4)
chmod +x *.sh
./init-db.sh                    # production: chỉ schema
# ./init-db.sh --with-demo-seed # server demo: thêm tài khoản demo (mật khẩu công khai!)
```

`init-db.sh` không bao giờ xoá hay tạo lại `StreetBizDB` đã có. Nó tạo login `DB_APP_USER` chỉ có quyền
đọc/ghi dữ liệu trong schema `dbo` (không sửa được cấu trúc bảng). API luôn dùng login này, không dùng `sa`.

Sau đó deploy lần đầu: push lên `main`, hoặc vào *Actions → Deploy API to VPS → Run workflow*.

## 4. Cấu hình của API (`api.env`)

Tên có `__` tương ứng với dấu `:` trong `appsettings.json`. Tạo khoá mới bằng `openssl rand -base64 48`,
và **không dùng lại** các khoá `dev-…` từng nằm trong lịch sử git.

### Bắt buộc: thiếu thì API không khởi động

| Tên | Ghi chú |
|---|---|
| `Jwt__SigningKey` | ≥ 32 ký tự. Đổi khoá sẽ đăng xuất mọi người. |
| `Permit__SigningKey` | ≥ 32 ký tự. Ký mã QR giấy phép; đổi khoá làm QR đã in không còn quét được. |
| `OrderPickup__SigningKey` | ≥ 32 ký tự. Ký mã nhận hàng. |
| `Otp__HashKey` | ≥ 32 ký tự. |

Các biến sau được `docker-compose.prod.yml` tự đặt, **không** cần ghi vào `api.env`:
`ASPNETCORE_ENVIRONMENT=Production`, `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (để giới hạn đăng nhập
thấy IP thật của người dùng sau Caddy), `STREETBIZ_DB_CONNECTION`, `Storage__Provider=Local`.

### Bắt buộc để sản phẩm chạy đúng

| Tên | Thiếu thì sao |
|---|---|
| `Cors__AllowedOrigins__0` | Domain FE trên Vercel, ví dụ `https://streetbiz.vercel.app`, không có `/` ở cuối. Thiếu thì trình duyệt chặn mọi request từ FE. Thêm `__1`, `__2` cho domain riêng hoặc domain preview. |
| `Permit__PublicScanBaseUrl` | Domain FE. QR in trên bảng giá trỏ tới `{domain}/q/{token}`. |
| `Sms__Provider`, `Sms__Endpoint`, `Sms__ApiKey` | `Http` và endpoint của nhà cung cấp SMS. Thiếu thì OTP (đăng ký, quên mật khẩu) lỗi. |

### Tuỳ chọn

| Tên | Khi nào cần |
|---|---|
| `Payments__…` | payOS/MoMo, xem [payos-payments.md](payos-payments.md). Giữ `Payments__SandboxEnabled=false` trên production. URL callback có dạng `https://<API_DOMAIN>/api/payments/...`. |
| `AiCompliance__FptAi__ApiKey`, `AiCompliance__Gemini__ApiKeys__0`, `AiCompliance__Groq__ApiKeys__0` | eKYC, kiểm tra hồ sơ bằng AI, chatbot. |
| `Geolocation__Boundaries__…` | Ranh giới phường đã xác minh. Thiếu thì kiểm tra geofence (SYS-02) trả 503. |
| `Nominatim__UserAgent` | Tên kèm email liên hệ, theo chính sách của Nominatim. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Theo dõi lỗi qua Application Insights (dùng được từ mọi host). Không đặt thì tắt. |

File upload (ảnh món, ảnh ô, giấy tờ, bằng chứng) lưu trên volume `streetbiz_uploads` của VPS, giữ nguyên
qua mọi lần deploy và khởi động lại. File luôn được API trả về sau khi kiểm tra quyền, không có URL công khai
trực tiếp. Chế độ Azure Blob trong code vẫn còn (`Storage__Provider=AzureBlob`) nhưng không dùng nữa.

## 5. Frontend trên Vercel

Không đổi cách deploy FE. Chỉ cần trỏ FE về API mới trong *Vercel → Project → Settings → Environment
Variables*, rồi **Redeploy** (biến `VITE_*` được đóng gói lúc build):

| Biến | Giá trị |
|---|---|
| `VITE_API_BASE_URL` | `https://<API_DOMAIN>/api` |
| `VITE_APP_ENV` | `production` (hoặc để trống) |

Bên BE, `Cors__AllowedOrigins__0` phải là đúng domain Vercel.

## 6. Vận hành

| Việc | Lệnh (trong `/opt/streetbiz`) |
|---|---|
| Xem trạng thái | `docker compose -f docker-compose.prod.yml ps` |
| Xem log API | `docker compose -f docker-compose.prod.yml logs -f --tail 200 api` |
| Khởi động lại API | `docker compose -f docker-compose.prod.yml restart api` |
| Quay về bản trước | *Actions → Deploy API to VPS → Run workflow*, nhập `image_tag` là SHA của commit muốn quay về. Hoặc trên server: `docker login ghcr.io` rồi `./deploy.sh <sha>`. |
| Kiểm tra sức khoẻ | `curl https://<API_DOMAIN>/health` trả `Healthy` |

`deploy.sh` tự quay về tag trước nếu bản mới không trả `/health` trong khoảng 2 phút, và in 60 dòng log
cuối của API để biết lý do.

### Sao lưu

Chạy hằng đêm bằng cron của user `deploy` (`crontab -e`):

```cron
30 2 * * * /opt/streetbiz/backup.sh >> /opt/streetbiz/backups/backup.log 2>&1
```

Script tạo `backups/StreetBizDB-<thời gian>.bak.gz` và `backups/uploads-<thời gian>.tar.gz`, xoá bản cũ hơn
`BACKUP_KEEP_DAYS` ngày. **Nên copy thư mục `backups/` sang nơi khác** (máy khác, Google Drive, object
storage…): bản sao lưu nằm cùng ổ đĩa sẽ mất theo nếu VPS hỏng.

Khôi phục database từ một bản sao lưu:

```bash
cd /opt/streetbiz && set -a && . ./.env && set +a
docker compose -f docker-compose.prod.yml stop api
gunzip -k backups/StreetBizDB-<thời gian>.bak.gz
docker compose -f docker-compose.prod.yml exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa \
  -P "$SQLSERVER_SA_PASSWORD" -C -Q "RESTORE DATABASE [StreetBizDB] FROM DISK = N'/var/opt/mssql/backups/StreetBizDB-<thời gian>.bak' WITH REPLACE"
./init-db.sh        # gắn lại login của app với database vừa khôi phục
docker compose -f docker-compose.prod.yml start api
```

Khôi phục file upload: `docker run --rm -v streetbiz_uploads:/data -v "$PWD/backups:/in" alpine:3.20 tar -xzf /in/uploads-<thời gian>.tar.gz -C /data`.

### Thay đổi schema trên database đang chạy

Database là database-first: API không bao giờ tự tạo hay sửa bảng. Theo quy ước của repo
([database.md § Changing the database](database.md#changing-the-database)), mọi thay đổi schema được sửa
thẳng vào `db/StreetBiz_SQL_Server.sql`, không thêm file `ALTER` riêng. Database production thì đã có dữ
liệu thật, không dựng lại được, nên:

1. PR đổi schema ghi kèm **trong mô tả PR** các câu lệnh `ALTER` tương ứng (có kiểm tra
   `IF COL_LENGTH(...) IS NULL` / `IF OBJECT_ID(...) IS NULL` để chạy lại vẫn an toàn).
2. **Trước khi** deploy code cần đến thay đổi đó: sao lưu, rồi chạy các câu lệnh trên server.

```bash
./backup.sh
docker compose -f docker-compose.prod.yml exec -it sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa \
  -P "$SQLSERVER_SA_PASSWORD" -C -I -d StreetBizDB
# dán các câu lệnh ALTER, kết thúc bằng GO
```

Các script nâng cấp một lần có sẵn trong `db/` (`StreetBiz_Upgrade_*.sql`) được workflow copy lên
`/opt/streetbiz/db` (container thấy ở `/scripts`), chạy bằng `-i /scripts/<tên file>` nếu cần.

## 7. Kiểm tra sau deploy

- `https://<API_DOMAIN>/health` trả `Healthy`; trình duyệt báo chứng chỉ hợp lệ.
- Swagger tắt trên production (`/swagger` trả 404), đó là bình thường.
- Đăng nhập từ FE trên Vercel không gặp lỗi CORS trong console. Chat/đơn hàng realtime kết nối được (WebSocket qua Caddy).
- Upload một ảnh, rồi `docker compose -f docker-compose.prod.yml restart api`: ảnh vẫn còn.
- Từ máy ngoài, cổng 1433 của VPS **không** kết nối được (`nc -vz <IP-VPS> 1433` thất bại).

Dữ liệu demo có tài khoản với mật khẩu công khai `Password123!`. Đổi mật khẩu hoặc xoá các tài khoản đó trước
khi cho người ngoài dùng.
