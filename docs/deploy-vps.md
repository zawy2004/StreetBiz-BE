# Deploy API và database lên VPS

**Backend (API .NET) và database (SQL Server) chạy trên một VPS** bằng Docker Compose. **Frontend chạy
trên Vercel.** Hướng dẫn này đi từ lúc mua VPS đến lúc web chạy thật, rồi đến cách vận hành hằng ngày:
cập nhật, quay về bản cũ, sao lưu và đổi schema.

```
Trình duyệt ──https──> Vercel (web StreetBiz-FE)
     │
     └──https / wss──> VPS
                        ├─ caddy      :80, :443  HTTPS tự động (Let's Encrypt), chuyển tiếp vào api
                        ├─ api        :8080      image ghcr.io/<owner>/streetbiz-api:<commit>
                        │                        volume streetbiz_uploads (ảnh, giấy tờ)
                        └─ sqlserver  :1433      SQL Server 2022 Express, chỉ trong mạng nội bộ
                                                 volume streetbiz_sqlserver-data
```

- Chỉ `caddy` mở cổng ra Internet (80, 443). SQL Server **không** mở cổng nào ra ngoài.
- API dùng một tài khoản SQL riêng, chỉ được đọc/ghi dữ liệu, **không dùng `sa`**.
- Mỗi lần push lên `main`, GitHub Actions chạy test, build image, rồi deploy lên VPS qua SSH. Nếu bản mới
  không khỏe, nó **tự quay về bản trước**.

## Mục lục

1. [Cần chuẩn bị](#1-cần-chuẩn-bị)
2. [Cài đặt VPS lần đầu](#2-cài-đặt-vps-lần-đầu)
3. [SSH key cho GitHub Actions](#3-ssh-key-cho-github-actions)
4. [Cấu hình và tạo database](#4-cấu-hình-và-tạo-database)
5. [Cấu hình GitHub](#5-cấu-hình-github)
6. [Deploy lần đầu](#6-deploy-lần-đầu)
7. [Frontend trên Vercel](#7-frontend-trên-vercel)
8. [Kiểm tra sau deploy](#8-kiểm-tra-sau-deploy)
9. [Vận hành hằng ngày](#9-vận-hành-hằng-ngày)
10. [Sao lưu và khôi phục](#10-sao-lưu-và-khôi-phục)
11. [Thay đổi schema trên database đang chạy](#11-thay-đổi-schema-trên-database-đang-chạy)
12. [Xử lý sự cố](#12-xử-lý-sự-cố)
13. [Danh sách kiểm tra bảo mật](#13-danh-sách-kiểm-tra-bảo-mật)

Các file chạy trên server nằm trong [`deploy/vps/`](../deploy/vps/):

| File | Vai trò |
|---|---|
| `docker-compose.prod.yml` | 3 container `caddy`, `api`, `sqlserver` và các volume. |
| `Caddyfile` | HTTPS cho tên miền API, chuyển tiếp mọi request (kể cả WebSocket của SignalR) vào `api`. |
| `.env.example` | Mẫu `.env`: tên miền, tag image, mật khẩu SQL. Chỉ docker compose đọc file này. |
| `api.env.example` | Mẫu `api.env`: khoá ký, CORS, SMS, payOS, AI. Chỉ container `api` đọc file này. |
| `init-db.sh` | Tạo `StreetBizDB` lần đầu và tài khoản SQL riêng cho API. Chạy lại an toàn. |
| `deploy.sh` | Kéo image theo tag, khởi động lại, chờ `/health`; lỗi thì quay về tag trước. |
| `backup.sh` | Sao lưu database và file upload, xoá bản cũ hơn `BACKUP_KEEP_DAYS` ngày. |

Workflow: [`.github/workflows/deploy-vps.yml`](../.github/workflows/deploy-vps.yml).

---

## 1. Cần chuẩn bị

| Thứ cần có | Yêu cầu |
|---|---|
| **VPS** | Ubuntu **24.04 LTS** (22.04 cũng được). Chip **x86_64 / amd64**: **không chọn gói ARM**, vì SQL Server và image API chỉ có bản amd64. **Tối thiểu 2 vCPU, 4 GB RAM** (SQL Server không khởi động nếu có dưới 2 GB), 40 GB SSD, 1 IPv4 riêng, có quyền root. |
| **Tên miền cho API** | Ví dụ `api.tenmien.vn`. Có thể là tên miền con của tên miền bạn đã có. |
| **Trang web trên Vercel** | Project StreetBiz-FE đã deploy, ví dụ `https://streetbiz.vercel.app`. |
| **Trên máy Windows của bạn** | **Git Bash** (cài cùng Git for Windows): có sẵn `ssh`, `ssh-keygen`, `ssh-keyscan`, `scp`. |
| **Quyền admin repo GitHub** | Để thêm secret và environment cho StreetBiz-BE. |
| Tuỳ chọn | Cổng gửi SMS (để có OTP), tài khoản payOS (chuyển khoản VietQR), khoá API AI (FPT.AI, Gemini, Groq). |

Ghi lại các giá trị sau, các bước sau dùng đến:

```
<IP-VPS>      ví dụ 103.20.30.40
<API_DOMAIN>  ví dụ api.tenmien.vn
<FE_DOMAIN>   ví dụ https://streetbiz.vercel.app   (có https://, không có / ở cuối)
```

**Trỏ tên miền ngay khi có IP.** Ở trang quản lý DNS, tạo bản ghi **A**: tên `api` (hoặc tên bạn chọn),
giá trị `<IP-VPS>`. Kiểm tra từ Git Bash: `nslookup <API_DOMAIN>` phải trả đúng `<IP-VPS>`. DNS có thể mất
vài phút đến vài giờ. Caddy chỉ xin được chứng chỉ HTTPS khi tên miền đã trỏ đúng.

## 2. Cài đặt VPS lần đầu

Đăng nhập bằng tài khoản root mà nhà cung cấp gửi (Git Bash):

```bash
ssh root@<IP-VPS>
```

Chạy lần lượt các khối lệnh sau **trên VPS**.

**Cập nhật hệ điều hành, đặt múi giờ Việt Nam:**

```bash
apt-get update && apt-get -y upgrade
timedatectl set-timezone Asia/Ho_Chi_Minh
```

**Cài Docker Engine và Docker Compose** (từ kho chính thức của Docker, không dùng gói `docker.io` của Ubuntu):

```bash
apt-get install -y ca-certificates curl
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
  > /etc/apt/sources.list.d/docker.list
apt-get update && apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin
docker --version && docker compose version     # phải in ra phiên bản
```

**Tạo user `deploy`** để chạy ứng dụng (không dùng root), cho nó dùng được Docker:

```bash
adduser --disabled-password --gecos "" deploy
usermod -aG docker deploy
mkdir -p /opt/streetbiz/backups /opt/streetbiz/db
chown -R deploy:deploy /opt/streetbiz
```

**Tạo swap 2 GB**, để hệ điều hành không tắt SQL Server khi thiếu RAM:

```bash
fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile
echo '/swapfile none swap sw 0 0' >> /etc/fstab
free -h                                         # dòng Swap phải có 2.0Gi
```

**Tường lửa:** chỉ mở SSH, HTTP, HTTPS. **Không mở 1433.**

```bash
ufw allow OpenSSH && ufw allow 80/tcp && ufw allow 443/tcp && ufw allow 443/udp
ufw --force enable && ufw status
```

Docker có thể vượt qua luật `ufw` với cổng mà container publish. Ở đây chỉ `caddy` publish 80/443 (đã mở sẵn),
còn SQL Server không publish cổng nào, nên không có gì bị lộ thêm.

**Tự cài bản vá bảo mật:**

```bash
apt-get install -y unattended-upgrades && dpkg-reconfigure -f noninteractive unattended-upgrades
```

## 3. SSH key cho GitHub Actions

GitHub Actions đăng nhập VPS bằng một SSH key **chỉ dùng để deploy**. Bạn cũng dùng key này để copy file lên
server.

**Trên máy của bạn** (Git Bash, trong thư mục bạn muốn cất key, **không** đặt trong repo):

```bash
ssh-keygen -t ed25519 -C "streetbiz-deploy" -f streetbiz_deploy   # hỏi passphrase: nhấn Enter 2 lần
cat streetbiz_deploy.pub                                          # copy dòng này
```

**Trên VPS** (vẫn là root), dán dòng vừa copy vào chỗ `<public key>`:

```bash
install -d -m 700 -o deploy -g deploy /home/deploy/.ssh
echo '<public key>' >> /home/deploy/.ssh/authorized_keys
chown deploy:deploy /home/deploy/.ssh/authorized_keys && chmod 600 /home/deploy/.ssh/authorized_keys
```

**Thử lại từ máy của bạn:**

```bash
ssh -i streetbiz_deploy deploy@<IP-VPS> docker ps     # phải chạy được, không hỏi mật khẩu
```

Khi lệnh trên đã chạy được, nên tắt đăng nhập SSH bằng mật khẩu. Trên VPS, sửa `/etc/ssh/sshd_config` (và các
file trong `/etc/ssh/sshd_config.d/`) thành `PasswordAuthentication no`, rồi `systemctl restart ssh`. Trước
khi đóng phiên đang mở, hãy thử đăng nhập lại ở một cửa sổ khác.

## 4. Cấu hình và tạo database

### 4.1. Copy file lên server

Trên máy của bạn, trong Git Bash, tại thư mục `StreetBiz-BE` (nhánh `develop` hoặc `main` mới nhất). Thay
`<đường-dẫn-key>` bằng đường dẫn tới file `streetbiz_deploy`:

```bash
K=<đường-dẫn-key>/streetbiz_deploy
scp -i $K deploy/vps/* deploy/vps/.env.example deploy@<IP-VPS>:/opt/streetbiz/
scp -i $K db/*.sql deploy@<IP-VPS>:/opt/streetbiz/db/
ssh -i $K deploy@<IP-VPS>
```

Từ đây trở đi, các lệnh chạy **trên VPS, bằng user `deploy`**, trong `/opt/streetbiz`.

### 4.2. Tạo `.env` (cho docker compose)

```bash
cd /opt/streetbiz
cp .env.example .env && chmod 600 .env
echo "Sb-$(openssl rand -hex 12)"     # chạy 2 lần: lấy 2 mật khẩu cho 2 dòng bên dưới
nano .env
```

| Biến | Giá trị |
|---|---|
| `API_DOMAIN` | `<API_DOMAIN>`, không có `https://` |
| `SQLSERVER_SA_PASSWORD` | Mật khẩu thứ nhất |
| `DB_APP_PASSWORD` | Mật khẩu thứ hai (tài khoản SQL của API) |
| Các dòng còn lại | Giữ mặc định. `IMAGE_TAG` được `deploy.sh` tự cập nhật mỗi lần deploy. |

Mật khẩu SQL Server cần ít nhất 8 ký tự gồm chữ hoa, chữ thường và số. Lệnh `echo "Sb-$(openssl rand -hex 12)"`
cho đúng dạng đó. **Không dùng `$`, `;`, `'` hay `"`** trong mật khẩu: `$` bị docker compose hiểu thành biến,
`;` làm hỏng chuỗi kết nối.

### 4.3. Tạo `api.env` (cấu hình của API)

```bash
cp api.env.example api.env && chmod 600 api.env
openssl rand -base64 48      # chạy 4 lần: lấy 4 khoá cho 4 dòng "Bắt buộc"
openssl rand -base64 32      # chạy 1 lần: khoá cho Payments__PayOs__CredentialKey
nano api.env
```

Tên biến dùng `__` thay cho `:` trong `appsettings.json` (`Jwt__SigningKey` là `Jwt:SigningKey`). Luôn tạo khoá
mới trên server; **không dùng lại** khoá trên máy của bạn hay khoá `dev-…` từng nằm trong lịch sử git.

**Bắt buộc: thiếu thì API không khởi động.**

| Biến | Ghi chú |
|---|---|
| `Jwt__SigningKey` | Khoá ký token đăng nhập. Đổi khoá sẽ đăng xuất mọi người. |
| `Permit__SigningKey` | Khoá ký mã QR giấy phép. **Đổi khoá làm mọi QR đã in không quét được nữa.** |
| `OrderPickup__SigningKey` | Khoá ký mã nhận hàng. |
| `Otp__HashKey` | Khoá băm mã OTP. |

**Cần để sản phẩm chạy đúng.**

| Biến | Giá trị và ý nghĩa |
|---|---|
| `Cors__AllowedOrigins__0` | `<FE_DOMAIN>`. Sai hoặc thiếu thì trình duyệt chặn mọi request từ web. Thêm `__1`, `__2` nếu có tên miền riêng. |
| `Permit__PublicScanBaseUrl` | `<FE_DOMAIN>`. QR in trên bảng giá trỏ tới `<FE_DOMAIN>/q/<mã>`. |
| `Sms__Provider` (+ `Sms__ApiKey`, `Sms__SecretKey` cho `Esms`; hoặc `Sms__Endpoint` cho `Http`) | Cổng gửi OTP, xem lưu ý bên dưới. Thiếu thì đăng ký tài khoản mới, quên mật khẩu và đăng nhập OTP báo lỗi; đăng nhập bằng mật khẩu vẫn chạy. |
| `Payments__PayOs__CredentialKey` | Kết quả `openssl rand -base64 32`. Thiếu thì payOS tắt. **Đổi khoá sau này thì các kênh payOS đã kết nối không đọc được nữa**, mỗi hộ/phường phải kết nối lại. |
| `Payments__PayOs__PublicWebhookBaseUrl` | `https://<API_DOMAIN>` (không có `/api`). payOS gọi webhook về `https://<API_DOMAIN>/api/payments/payos/callback/...`. |
| `Payments__PayOs__FrontendBaseUrl` | `<FE_DOMAIN>`: trang payOS đưa người trả tiền quay về. |
| `Payments__SandboxEnabled` | `false` trên server thật. |

**Lưu ý về SMS.** Có hai cách gửi OTP, chọn bằng `Sms__Provider`:

- **`Esms` (eSMS.vn).** Đặt `Sms__ApiKey`, `Sms__SecretKey` (lấy ở "Quản lý API eSMS") và `Sms__SmsType=8` (mặc định): tin đầu số
  cố định, không cần đăng ký brandname; loại `2` cần `Sms__Brandname` đã đăng ký. `Sms__Sandbox=true` chỉ kiểm tra request,
  không gửi và không tính phí; trên server thật phải để `false`. Mã `100` chỉ nghĩa là eSMS đã nhận yêu cầu, không phải tin
  đã tới máy. Khi eSMS từ chối, log API ghi `CodeResult`: `101` sai key, `103` thiếu số dư, `104` brandname sai, `140` IP máy chủ
  chưa được eSMS cho phép.
- **`Http` (cổng khác).** API gửi `POST {Sms__Endpoint}` với body JSON `{"to": "<số điện thoại>", "from": "<Sms__Sender>",
  "message": "<nội dung>"}` và header `Authorization: Bearer <Sms__ApiKey>`, coi mọi mã 2xx là thành công. Các nhà cung cấp khác
  dùng định dạng riêng nên cần dịch vụ trung gian hoặc một adapter trong `StreetBiz.Infrastructure/Notifications`.

**Tuỳ chọn** (đã có sẵn dạng chú thích trong `api.env.example`; bỏ `#` ở đầu dòng để bật):

| Biến | Khi nào cần |
|---|---|
| `AiCompliance__FptAi__ApiKey`, `AiCompliance__FptAi__ApiUrl` | Đọc CCCD (eKYC) khi đăng ký hộ kinh doanh. |
| `AiCompliance__Gemini__ApiKeys__0`, `AiCompliance__Gemini__Model`, `AiCompliance__Groq__ApiKeys__0`, `AiCompliance__Groq__Model` | Kiểm tra hồ sơ bằng AI và chatbot. **Khoá nào cũng cần tên model đi kèm.** |
| `Alerts__DiscordWebhookUrl` | Báo lỗi thanh toán vào một kênh Discord ([payos-payments.md](payos-payments.md)). |
| `Geolocation__Boundaries__…` | Ranh giới phường đã xác minh ([ward-slot-workflows.md](ward-slot-workflows.md)). Thiếu thì kiểm tra vị trí trả 503. |
| `Nominatim__UserAgent` | Tên ứng dụng kèm email liên hệ, theo chính sách của OpenStreetMap Nominatim. |

`docker-compose.prod.yml` tự đặt các biến sau, **không** ghi vào `api.env`: `ASPNETCORE_ENVIRONMENT=Production`,
`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (để API thấy IP thật của người dùng sau Caddy), và chuỗi kết nối
database `STREETBIZ_DB_CONNECTION`.

### 4.4. Tạo database

```bash
chmod +x *.sh
./init-db.sh --with-demo-seed      # bản demo đồ án: schema + dữ liệu và tài khoản demo
# ./init-db.sh                     # chỉ schema, không có tài khoản nào
```

Script khởi động SQL Server, tạo `StreetBizDB` từ `db/StreetBiz_SQL_Server.sql`, rồi tạo login `DB_APP_USER` chỉ có
quyền đọc/ghi dữ liệu trong schema `dbo` (không sửa được cấu trúc bảng). Script **không bao giờ xoá hay tạo lại**
`StreetBizDB` đã có, nên chạy lại vẫn an toàn.

**Nên chọn `--with-demo-seed`.** Script schema chỉ có dữ liệu danh mục (vai trò, danh sách phường, loại vi phạm), **không có
tài khoản quản trị nền tảng hay cán bộ phường nào**, và app không có chức năng tạo tài khoản quản trị đầu tiên.
Với demo seed:

- mọi tài khoản demo dùng chung mật khẩu công khai `Password123!`. **Đổi mật khẩu** từng tài khoản (trong trang tài
  khoản của app) trước khi đưa link cho người ngoài;
- QR của các giấy phép demo là giá trị giữ chỗ (`SEED-PERMIT-…`), quét sẽ báo không hợp lệ. Giấy phép cấp mới trên
  server thì quét được bình thường;
- các file giấy tờ demo không có trên server, nên xem trước giấy tờ của hồ sơ demo sẽ báo không tìm thấy. File tải
  lên sau này thì hiển thị bình thường.

Kiểm tra: `docker compose -f docker-compose.prod.yml ps` phải thấy `sqlserver` ở trạng thái `healthy`.

## 5. Cấu hình GitHub

Trong repo **StreetBiz-BE**: *Settings → Secrets and variables → Actions*.

| Loại | Tên | Giá trị |
|---|---|---|
| Secret | `VPS_HOST` | `<IP-VPS>` |
| Secret | `VPS_USER` | `deploy` |
| Secret | `VPS_SSH_KEY` | Toàn bộ nội dung file **private key** `streetbiz_deploy` (cả dòng `-----BEGIN…` và `-----END…`) |
| Secret | `VPS_KNOWN_HOSTS` | Kết quả của `ssh-keyscan <IP-VPS>` chạy trên máy bạn. Nhờ đó workflow từ chối kết nối nếu có server giả mạo. |
| Secret (tuỳ chọn) | `VPS_PORT` | Cổng SSH, nếu khác 22 |
| Variable (tuỳ chọn) | `VPS_PATH` | Thư mục trên server, mặc định `/opt/streetbiz` |

`VPS_HOST` phải đúng là giá trị bạn đã dùng với `ssh-keyscan`; nếu không, SSH sẽ từ chối host key.

Thêm một environment tên **`production`** (*Settings → Environments → New environment*). Có thể bật **Required
reviewers** để mỗi lần deploy cần một người bấm duyệt.

Không cần token nào cho registry: workflow đẩy image lên `ghcr.io` bằng `GITHUB_TOKEN` của chính nó, và mỗi lần
deploy đăng nhập GHCR trên server bằng token đó (hết hạn khi job kết thúc) rồi đăng xuất. Image để private cũng
được.

## 6. Deploy lần đầu

Workflow chỉ chạy được khi file `deploy-vps.yml` đã nằm trên nhánh **`main`**.

1. Trên GitHub, tạo pull request từ `develop` vào `main` rồi merge.
2. Mở tab **Actions → Deploy API to VPS**. Workflow chạy 3 bước:
   - `test`: kiểm tra đủ secret, rồi chạy toàn bộ test;
   - `build`: build image và đẩy lên `ghcr.io/<owner>/streetbiz-api:<commit>`;
   - `deploy`: copy `deploy/vps` và `db/*.sql` lên server, rồi chạy `deploy.sh <commit>`.
3. Nếu bật Required reviewers, bấm **Review deployments → Approve** khi được hỏi.
4. Lần đầu, Caddy xin chứng chỉ HTTPS nên có thể mất thêm 1 phút. `deploy.sh` chờ tối đa khoảng 2 phút để
   `https://<API_DOMAIN>/health` trả lời.

Từ đó về sau, mỗi lần merge vào `main` là tự deploy. Muốn chạy lại bằng tay: *Actions → Deploy API to VPS → Run
workflow* (để trống `image_tag`).

## 7. Frontend trên Vercel

Không đổi cách deploy web. Chỉ cần trỏ web về API mới: vào *Vercel → Project → Settings → Environment Variables*
(môi trường **Production**):

| Biến | Giá trị |
|---|---|
| `VITE_API_BASE_URL` | `https://<API_DOMAIN>/api` |
| `VITE_APP_ENV` | `production` |

Sau đó vào *Deployments*, chọn bản mới nhất, bấm **Redeploy**. Biến `VITE_*` được đóng gói lúc build, nên chỉ sửa
biến mà không redeploy thì web vẫn gọi địa chỉ cũ.

Bên API, `Cors__AllowedOrigins__0` phải đúng là tên miền Vercel (`<FE_DOMAIN>`). Nếu đổi tên miền web, sửa `api.env`
rồi khởi động lại API (mục 9).

## 8. Kiểm tra sau deploy

- [ ] `https://<API_DOMAIN>/health` trả `Healthy`, và trình duyệt báo chứng chỉ hợp lệ.
- [ ] `https://<API_DOMAIN>/swagger` trả 404. Đó là bình thường: Swagger tắt trên production.
- [ ] Mở `<FE_DOMAIN>` và đăng nhập (ví dụ tài khoản demo `0905000101`). Console của trình duyệt (F12) không có
      lỗi CORS.
- [ ] Chat hoặc trạng thái đơn hàng cập nhật ngay, không cần tải lại trang (WebSocket qua Caddy chạy được).
- [ ] Tải lên một ảnh, chạy `docker compose -f docker-compose.prod.yml restart api` trên server: ảnh vẫn còn.
- [ ] Từ máy của bạn, cổng 1433 của VPS **không** kết nối được: `nc -vz <IP-VPS> 1433` (Git Bash) hoặc
      `Test-NetConnection <IP-VPS> -Port 1433` (PowerShell) phải thất bại.
- [ ] Đã đổi mật khẩu các tài khoản demo.
- [ ] Đã cài lịch sao lưu (mục 10).

## 9. Vận hành hằng ngày

Mọi lệnh chạy trên VPS bằng user `deploy`, trong `/opt/streetbiz`. Để gõ ngắn hơn:

```bash
alias dc='docker compose -f /opt/streetbiz/docker-compose.prod.yml'
echo "alias dc='docker compose -f /opt/streetbiz/docker-compose.prod.yml'" >> ~/.bashrc
```

| Việc | Lệnh |
|---|---|
| Xem trạng thái | `dc ps` |
| Xem log API | `dc logs -f --tail 200 api` |
| Xem log Caddy (HTTPS) | `dc logs --tail 100 caddy` |
| Khởi động lại API (sau khi sửa `api.env`) | `dc up -d api` |
| Kiểm tra sức khoẻ | `curl https://<API_DOMAIN>/health` |
| Dung lượng ổ đĩa | `df -h /` và `docker system df` |

**Cập nhật phiên bản mới:** merge `develop` vào `main` trên GitHub. Workflow tự test, build và deploy. Nếu bản mới
không trả `/health` trong khoảng 2 phút, `deploy.sh` in 60 dòng log cuối của API, quay về tag trước, và workflow báo
đỏ.

**Quay về một bản cũ:**

- Cách dễ nhất: *Actions → Deploy API to VPS → Run workflow*, nhập `image_tag` là mã commit (SHA đầy đủ) của bản
  muốn quay về. Workflow bỏ qua test và build, chỉ deploy image đã có.
- Trên server: `docker login ghcr.io -u <github-user>` (mật khẩu là một Personal Access Token có quyền
  `read:packages`), rồi `./deploy.sh <SHA>`, rồi `docker logout ghcr.io`.

`deploy.sh` không đụng tới database. Nếu bản mới có đổi schema, xem mục 11 trước khi quay về.

## 10. Sao lưu và khôi phục

**Cài lịch sao lưu hằng đêm** (user `deploy`, `crontab -e`, thêm dòng sau):

```cron
30 2 * * * /opt/streetbiz/backup.sh >> /opt/streetbiz/backups/backup.log 2>&1
```

Mỗi lần chạy tạo `backups/StreetBizDB-<thời gian>.bak.gz` (database) và `backups/uploads-<thời gian>.tar.gz` (file
upload), và xoá bản cũ hơn `BACKUP_KEEP_DAYS` ngày (mặc định 14). Chạy thử ngay: `./backup.sh && ls -lh backups`.

**Copy bản sao lưu ra khỏi VPS.** Bản sao lưu nằm cùng ổ đĩa sẽ mất theo nếu VPS hỏng. Từ máy của bạn (Git Bash):

```bash
scp -i $K "deploy@<IP-VPS>:/opt/streetbiz/backups/*.gz" ./streetbiz-backups/
```

hoặc dùng `rclone` trên VPS để đẩy lên Google Drive hay object storage.

**Khôi phục database** từ một bản sao lưu:

```bash
cd /opt/streetbiz && set -a && . ./.env && set +a
f=StreetBizDB-<thời gian>.bak
docker compose -f docker-compose.prod.yml stop api
gunzip -k "backups/$f.gz" && chmod 644 "backups/$f"
docker compose -f docker-compose.prod.yml exec -T sqlserver mkdir -p /var/opt/mssql/backups
docker compose -f docker-compose.prod.yml cp "backups/$f" "sqlserver:/var/opt/mssql/backups/$f"
docker compose -f docker-compose.prod.yml exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa \
  -P "$SQLSERVER_SA_PASSWORD" -C -Q "RESTORE DATABASE [StreetBizDB] FROM DISK = N'/var/opt/mssql/backups/$f' WITH REPLACE"
docker compose -f docker-compose.prod.yml exec -T -u 0 sqlserver rm -f "/var/opt/mssql/backups/$f"
rm "backups/$f"
./init-db.sh        # gắn lại tài khoản SQL của API với database vừa khôi phục
docker compose -f docker-compose.prod.yml start api
```

**Khôi phục file upload:**

```bash
docker run --rm -v streetbiz_uploads:/data -v "$PWD/backups:/in" alpine:3.20 tar -xzf /in/uploads-<thời gian>.tar.gz -C /data
```

**Chuyển sang VPS mới:** làm mục 2–5 trên VPS mới, copy hai file sao lưu mới nhất sang, khôi phục như trên, trỏ DNS về
IP mới, cập nhật `VPS_HOST` và `VPS_KNOWN_HOSTS` trên GitHub, rồi chạy lại workflow.

## 11. Thay đổi schema trên database đang chạy

Database là database-first: API không bao giờ tự tạo hay sửa bảng. Theo quy ước của repo
([database.md § Changing the database](database.md#changing-the-database)), mọi thay đổi schema được sửa thẳng vào
`db/StreetBiz_SQL_Server.sql`. Database trên VPS đã có dữ liệu thật, không dựng lại được, nên:

1. PR có đổi schema ghi kèm **trong mô tả PR** các câu lệnh `ALTER` tương ứng, có kiểm tra
   `IF COL_LENGTH(...) IS NULL` / `IF OBJECT_ID(...) IS NULL` để chạy lại vẫn an toàn.
2. **Trước khi** deploy code cần thay đổi đó: sao lưu, rồi chạy các câu lệnh trên server bằng `sa`:

```bash
cd /opt/streetbiz && set -a && . ./.env && set +a
./backup.sh
docker compose -f docker-compose.prod.yml exec -it sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa \
  -P "$SQLSERVER_SA_PASSWORD" -C -I -d StreetBizDB
# dán các câu lệnh ALTER, kết thúc bằng một dòng GO
```

Một file `.sql` đã có trong `db/` (workflow copy lên `/opt/streetbiz/db`, container thấy ở `/scripts`) thì chạy bằng
`... sqlcmd ... -f 65001 -i /scripts/<tên file>`. Luôn có `-f 65001` để tiếng Việt được đọc đúng UTF-8.

## 12. Xử lý sự cố

| Triệu chứng | Nguyên nhân thường gặp | Cách xử lý |
|---|---|---|
| Workflow đỏ ở bước *Check deploy settings* | Thiếu secret | Thêm secret mà thông báo lỗi nêu tên (mục 5). |
| `Host key verification failed` | `VPS_KNOWN_HOSTS` không khớp `VPS_HOST`, hoặc VPS đã cài lại | Chạy lại `ssh-keyscan <IP-VPS>` và cập nhật secret. |
| `Permission denied (publickey)` | Sai private key, hoặc thiếu `authorized_keys` | Làm lại mục 3, thử `ssh -i streetbiz_deploy deploy@<IP-VPS>`. |
| `deploy.sh` báo không khỏe, log có `is not configured` | Thiếu biến bắt buộc trong `api.env` | Thêm biến (mục 4.3), rồi chạy lại workflow. |
| Không có HTTPS; log Caddy có `challenge failed` hay `NXDOMAIN` | DNS chưa trỏ đúng, hoặc cổng 80/443 bị chặn | `nslookup <API_DOMAIN>`; kiểm tra `ufw status` và tường lửa của nhà cung cấp VPS. |
| `sqlserver` khởi động lại liên tục | Thiếu RAM, hoặc mật khẩu `sa` quá yếu | `dc logs sqlserver`; kiểm tra swap (`free -h`) và `SQLSERVER_SA_PASSWORD`. |
| API log `Login failed for user 'streetbiz_app'` | Chưa chạy `init-db.sh`, hoặc vừa đổi `DB_APP_PASSWORD` | Chạy `./init-db.sh` (an toàn khi chạy lại), rồi `dc up -d api`. |
| Web báo lỗi CORS | `Cors__AllowedOrigins__0` không đúng tên miền Vercel | Sửa `api.env` (không có `/` ở cuối), rồi `dc up -d api`. |
| Web vẫn gọi API cũ | Vercel chưa redeploy sau khi đổi biến | Redeploy trên Vercel (mục 7). |
| Đăng ký hoặc quên mật khẩu báo lỗi | Chưa cấu hình SMS | Xem lưu ý về SMS ở mục 4.3. |
| Upload báo 413 | File vượt giới hạn | API giới hạn 5 MB mỗi file; Caddy cho tối đa 12 MB mỗi request. |
| Đầy ổ đĩa | Image cũ, log, bản sao lưu | `docker system prune -f`; giảm `BACKUP_KEEP_DAYS`; copy bản sao lưu đi nơi khác. |

## 13. Danh sách kiểm tra bảo mật

- [ ] SSH chỉ đăng nhập bằng key (`PasswordAuthentication no`). Key deploy chỉ dùng cho việc deploy.
- [ ] `ufw` chỉ mở 22, 80, 443. Cổng 1433 không kết nối được từ ngoài.
- [ ] `.env` và `api.env` có quyền `600`, không bao giờ commit hay gửi qua chat.
- [ ] Các khoá trong `api.env` được tạo mới trên server, không dùng lại khoá trên máy dev.
- [ ] API dùng tài khoản `streetbiz_app`, không dùng `sa`.
- [ ] `Payments__SandboxEnabled=false`.
- [ ] Đã đổi mật khẩu mọi tài khoản demo.
- [ ] Sao lưu chạy hằng đêm và được copy ra khỏi VPS.
- [ ] `unattended-upgrades` đã bật.
