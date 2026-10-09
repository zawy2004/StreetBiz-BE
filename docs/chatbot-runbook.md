# StreetBiz Assistant — vận hành và nghiệm thu

> Bản sửa hỏi ảnh 08/10/2026: xem [luồng nhận diện nhanh và phục hồi phản hồi ảnh](chatbot-image-latency-fix.md). Luồng tìm món thông thường hiện dùng một lượt vision; so sánh ảnh là luồng riêng.

> Cập nhật 08/10/2026: xem [nâng cấp độ tin cậy và tìm món bằng ảnh](chatbot-reliability-upgrade.md) cho cấu hình demo bỏ daily admission, pool key độc lập, tìm quầy/đối chiếu ảnh và retry ảnh tạm. Các kết quả 07/10 phía dưới là lịch sử trước bản nâng cấp này.

## Assistant v2 (09/10/2026) — giọng nói, ảnh → món → quầy, giao diện "Ink & Ember"

Tóm tắt thay đổi: [chatbot-ui-upgrade.md](chatbot-ui-upgrade.md#v2--09102026), thiết kế voice: [chatbot-voice-design.md](chatbot-voice-design.md).

### Cấu hình `Chatbot:Voice` (không chứa secrets)

| Khóa | Mặc định | Ý nghĩa |
| --- | --- | --- |
| `Enabled` | `false` | Development đang đặt `true`. Capabilities chỉ báo `voiceEnabled` cho tài khoản đăng nhập khi có key Gemini và `Model` |
| `Model` | (trống) | Model Live native audio, ví dụ `gemini-3.8-live` theo tài liệu Live API ngày 09/10/2026. Kiểm tra lại trước khi đổi |
| `VoiceName` | `Kore` | Giọng dựng sẵn của provider |
| `MaxSessionSeconds` | `300` | Thời lượng tối đa một phiên |
| `DailySecondsPerUser` | `1200` | Hạn mức mỗi tài khoản/ngày Việt Nam, **đọc từ DB** (bảng `ChatbotAudits`, operation `voice_session`, outcome `"{giây}s:{lý do}"`) nên không reset khi khởi động lại |
| `IdleSeconds` | `45` | Dừng khi không ai nói; cảnh báo trước 10 giây |
| `GlobalConcurrentSessions` | `4` | Số phiên đồng thời mỗi instance |
| `SilenceDurationMs` | `700` | VAD: khoảng lặng coi là hết câu |
| `MaxToolCalls` | `12` | Số lần tra cứu mỗi phiên |
| `TicketSeconds` | `30` | Vé phiên dùng một lần |
| `MaxReconnects` | `2` | Số lần tự nối lại upstream bằng session-resumption handle (goAway/mạng) |

Luồng: `POST /api/chatbot/voice/sessions` (cấp vé, tạo hội thoại) → WebSocket `/hubs/chatbot-voice/{sessionId}?ticket=…&access_token=…` (JWT như SignalR; vé phải khớp user + phiên đăng nhập + scope) → relay .NET ↔ Gemini Live. Key gửi bằng header `x-goog-api-key`, không nằm trong URL. Không lưu âm thanh; mỗi lượt lưu phụ đề + thẻ dữ liệu như tin nhắn `channel = "VOICE"`, **không giữ chỗ token** và không bị chặn bởi `DailyTokenBudget` (voice có hạn mức phút riêng), nhưng token thực dùng vẫn được ghi.

Giới hạn còn lại: vé phiên và "một phiên/tài khoản" vẫn ở bộ nhớ từng instance. Nhiều instance cần sticky session cho WebSocket. Truy vấn phút/ngày quét `ChatbotAudits` theo actor; khi bảng lớn cân nhắc index `(actor_id, operation, timestamp)` (thay đổi schema, cần duyệt).

### Đo độ trễ và chi phí voice (thay cho ước lượng)

Mỗi phiên kết thúc gửi `summary` cho client (hiện dưới Orb: "Phiên 2:13 · 4 lượt · phản hồi ~1,2 giây") và ghi metric:

- `chatbot.voice.reply.seconds`: từ mẩu phụ đề cuối của người dùng tới khối audio đầu tiên của câu trả lời (xấp xỉ "nói xong → nghe thấy trả lời").
- `chatbot.voice.session.seconds`, `chatbot.voice.tokens` (input + output theo `usageMetadata` của provider), `chatbot.voice.reconnects`.

Quy trình trước khi bật cho nhiều người dùng: chạy 10–20 phiên thật trên điện thoại mục tiêu, ghi p50/p95 `replyMedianMs`/`replyP95Ms` và token. Chi phí = token audio vào × đơn giá vào + token audio ra × đơn giá ra (+ token text/tool) theo [bảng giá Gemini API](https://ai.google.dev/gemini-api/docs/pricing) **tại ngày đo**. Không suy chi phí chỉ từ số phút. Mục tiêu: p95 phản hồi ≤ 3 giây, dừng phát ≤ 200 ms sau khi ngắt lời.

Checklist voice: Chrome Android, Safari iOS (cần thao tác chạm trước khi phát âm thanh), loa ngoài và tai nghe (vọng tiếng), tiếng ồn phố, mạng 4G yếu, đăng xuất giữa phiên, ngắt lời liên tục, tab ẩn > 30 giây.

### Ảnh và tìm món

Tool `public.food` (chế độ ảnh) nhận `query`, tối đa 2 `alternatives`, `confidence`, `cues`; server tạo thẻ `dish_match` [AI]. Khi tên món không ra kết quả, server chỉ thử tên gọi trong [dish-synonyms.json](../src/StreetBiz.Application/Features/Chatbot/dish-synonyms.json) (có `version`; thêm nhóm tên mới tại đây, không để model tự bịa). Vị trí người dùng (khi bật "Gần tôi") được làm tròn ~100 m, chỉ dùng trong request và không vào hash idempotency hay lịch sử.

## Phạm vi

Trợ lý Core cho Guest, Customer, Vendor cố định/lưu động, Ward Authority và Platform Admin. Không có công cụ ghi nghiệp vụ, tự duyệt/từ chối, ghi vi phạm, thanh toán, hay cấp giấy phép. Ward và Admin là hai phạm vi độc lập. `/assistant` và widget dùng cùng một instance hội thoại; route trợ lý vendor cũ chuyển đến trang mới. Các endpoint OCR/eKYC cũ không bị thay thế.

Guest dùng hướng dẫn sản phẩm công khai, không lưu lịch sử server và không tiêu token provider. Các câu hỏi cá nhân rõ ràng dùng truy vấn xác định; dữ liệu trực tiếp được trình bày bằng thẻ/nhận xét dựa trên dữ liệu server, không để LLM tự viết số tiền hoặc hiệu lực giấy phép. Câu hỏi hướng dẫn/phức tạp dùng local tool calling và streaming. Không có kho luật đã duyệt: trợ lý không tự viện dẫn điều luật hoặc mức phạt từ trí nhớ.

## Cấu hình không chứa secrets

Backend đọc section `Chatbot` (hoặc biến môi trường với `__`). Mặc định:

| Khóa | Giá trị | Ý nghĩa |
| --- | --- | --- |
| `Enabled` | `true` | Tắt để dừng lượt hỏi mới; lịch sử/xóa vẫn khả dụng |
| `GuestEnabled` | `true` | FAQ công khai |
| `AttachmentsEnabled` | `false` | Chỉ bật sau khi kiểm thử Gemini và duyệt chính sách ảnh |
| `CrossProviderFallback` | `true` | Tối đa một provider dự phòng, chỉ trước khi có văn bản |
| `MaxQuestionCharacters` | `4000` | Giới hạn câu hỏi |
| `MaxOutputTokens` | `2000` | Giới hạn đầu ra mỗi lần gọi model |
| `MaxContextCharacters` | `18000` | Ngưỡng context ước tính, thêm giới hạn byte |
| `MaxToolCalls` / `MaxModelSteps` | `6` / `5` | Giới hạn vòng tool/model |
| `TurnTimeoutSeconds` | `75` | Deadline toàn lượt |
| `ProviderTimeoutSeconds` | `25` | Deadline từng lần gọi, cho phép dự phòng trước deadline toàn lượt |
| `ImageProviderTimeoutSeconds` | `25` | Deadline một attempt ảnh, lấy min với ProviderTimeoutSeconds |
| `MaxTurnTokenBudget` | `28000` | Dự trữ hạn mức cho lượt đang chạy |
| `DailyTokenBudget` | `100000` | Hạn mức kỹ thuật mỗi tài khoản/ngày Việt Nam |
| `EnforceDailyTokenBudget` | `true` | Development hiện đặt `false`; không xóa số liệu usage |
| `IndependentKeyQuotas` | `false` | Development hiện đặt `true` theo xác nhận các key độc lập quota |
| `GlobalConcurrentTurns` | `8` | Admission toàn process; tối đa hai lượt/tài khoản |
| `RetentionDays` | `30` | Hạn lưu nội dung; dọn theo batch mỗi 15 phút |

`Chatbot:Groq:Model` / `Chatbot:Gemini:Model` ưu tiên hơn `AiCompliance:{provider}:Model`. Keys dùng pool hiện hữu `AiCompliance:{provider}:ApiKeys` hoặc `ApiKey`. Chỉ cấu hình keys bằng user-secrets, secret manager hoặc biến môi trường; không đưa vào tài liệu, frontend, log hoặc database. Khóa từng lộ qua chat/source phải thu hồi và thay mới.

FE: `VITE_ENABLE_CHATBOT=true`, API base URL hiện có và `VITE_USE_MOCK_API=false` để dùng backend. Chế độ mock ghi rõ `[Mô phỏng]`, không gọi AI và không lưu server. Thay biến Vite cần khởi động lại dev server hoặc build lại.

Không tự đổi model dựa trên tên quảng cáo. Groq adapter là Chat Completions/local tools; Gemini adapter là GenerateContent/streamGenerateContent, giữ nguyên native thought signatures và function call IDs. Không trộn wire protocol Interactions API vào adapter GenerateContent. Tham khảo [Groq local tools](https://console.groq.com/docs/tool-use/local-tool-calling) và [Gemini function calling](https://ai.google.dev/gemini-api/docs/function-calling).

## Database và độ tin cậy

- Schema chính: `db/StreetBiz_SQL_Server.sql`; mapping: `ChatbotConfiguration.cs`. Ba bảng mới không liên quan bảng chat người-người.
- Database development `localhost/StreetBizDB` đã được bổ sung đúng ba bảng theo quyền người dùng ngày 07/10/2026. Xem ADR; không chạy lại toàn schema lên database có dữ liệu và không dùng `-Recreate` để kích hoạt chatbot.
- Lượt hỏi nằm trong request HTTP, không có job LLM chạy fire-and-forget. SignalR chỉ là kênh tiến độ; REST snapshot là nguồn chính thức.
- Idempotency theo conversation + client request ID + hash payload; nội dung khác cùng key trả 409. DB lease ngăn hai lượt đồng thời trong một conversation. Lease hết hạn được khôi phục khi đọc hội thoại.
- Hủy kết quả là terminal: lượt trả về muộn không ghi đè. Khi server chết/hủy, dự trữ token chưa xác định được tính bảo thủ, không hoàn lại để tránh lạm dụng.
- Xóa hội thoại xóa ngay payload/feedback, giữ số liệu metering tối thiểu đến dọn sau ngày hiện tại. Xóa không đặt lại hạn mức trong ngày. Audit chỉ lưu actor, thời điểm, tên thao tác và kết quả, không lưu prompt/tool output.
- Tất cả lịch sử kiểm tra owner + role/ward/vendor scope. Model không được cung cấp tham số userId/wardId. Tool và từng người nhận realtime được kiểm tra lại phiên. Role/phường thay đổi không mở được lịch sử scope cũ.
- Truy vấn danh sách vendor legacy dùng projection DB giới hạn 20; không trả CCCD, ảnh eKYC, QR payload, token hay thông tin callback. Các danh sách là một phần dữ liệu, không được hiểu như tổng toàn hệ thống.
- Bản triển khai này chỉ hỗ trợ **một API instance**. Trước scale-out cần backplane, cơ chế cancellation và rate-limit phân tán; memory ảnh không dùng được giữa các replica.

## Ảnh (opt-in)

Nâng cấp UI/ảnh mới nhất: xem [chatbot-ui-upgrade.md](chatbot-ui-upgrade.md). Development đã bật `Chatbot:AttachmentsEnabled=true`; môi trường khác vẫn mặc định false. Cần khởi động lại API để nạp settings. Capabilities chỉ hiện nút ảnh cho user đăng nhập khi Gemini có cấu hình model/key, không phải health check provider.

Chỉ tài khoản đăng nhập, có xác nhận rõ ảnh không chứa thông tin nhạy cảm và đồng ý gửi đến Gemini. Trình duyệt nhận PNG/JPEG/WebP tối đa 5 MB, thu nhỏ và xuất PNG <=1280px. Server kiểm tra signature, CRC, kích thước, bounded decompression, bỏ metadata; chỉ nhận PNG 8-bit RGB/RGBA không interlace <=2 MB. Không nhận URL, SVG/PDF, không có public file URL, không tái sử dụng bucket evidence/eKYC.

Ảnh giữ trong memory vault tối đa 5 phút, khóa theo user + session + scope, tối đa hai ảnh/tài khoản và 32 MB toàn process; đọc tiêu thụ một lần, xóa byte khi lượt kết thúc. Không đưa ảnh vào lịch sử hoặc database. FE giữ tối đa một ảnh của lượt thất bại trong RAM 5 phút để gửi lại sau khi xác nhận consent; hết hạn/reload cần chọn lại. Đây **không phải** hệ thống phát hiện mọi nội dung nhạy cảm trong pixel; xác nhận người dùng không thay thế rà soát chính sách dữ liệu trước khi bật production. Ảnh không chứng minh thanh toán, danh tính, kích thước geofence hoặc vi phạm pháp luật.

## Quan sát và xử lý lỗi

Meter `StreetBiz.Chatbot`: `chatbot.turns`, `chatbot.turn.seconds`, `chatbot.first_delta.seconds`, `chatbot.reserved_tokens`, `chatbot.fallbacks`, `chatbot.provider.errors`, và cho voice `chatbot.voice.reply.seconds`, `chatbot.voice.session.seconds`, `chatbot.voice.tokens`, `chatbot.voice.reconnects`. Tags giới hạn role/provider/status/category; không có user ID/câu hỏi. Kết nối meter này với collector hiện hữu trước khi đặt SLO production. Token thành công đối soát theo usage provider; trường hợp thiếu usage/ngắt kết nối vẫn ước lượng, **không phải hóa đơn provider**.

- 401: đăng nhập lại, kiểm tra phiên có bị thu hồi/đổi role không.
- 404: hội thoại không còn quyền xem, đã xóa/hết hạn hoặc tài nguyên không thuộc scope; không dò ID khác.
- 409: lấy snapshot của `activeMessageId`, chờ hoặc hủy lượt đó. Không gửi liên tiếp key mới.
- 429: tôn trọng Retry-After. Chỉ xoay sang key khác khi cấu hình xác nhận quota độc lập; quota dùng chung thì nghỉ cả provider.
- Provider lỗi cấu hình/401/403: kiểm tra model và key bằng thao tác riêng, không ghi raw response chứa thông tin nhạy cảm. Tắt adapter lỗi bằng model override rỗng nếu cần.
- Timeout/transient trước token đầu: tối đa một fallback; sau khi đã trả văn bản thì đánh dấu chưa hoàn tất, không nối hai provider vào một câu trả lời.
- Lỗi schema: xác minh ba bảng; không tự migrate/recreate từ startup.
- Ngắt WebSocket: UI phục hồi bằng REST/polling. Không cache chatbot vào localStorage/service worker; giấy phép phải kiểm tra live.

## Kiểm thử

```powershell
# Từ StreetBiz-BE: không gọi AI thật hay dùng database thật
dotnet test StreetBiz.Backend.sln --no-restore
# Từ StreetBiz-FE
npm run build
npm run lint
npm test
```

Smoke test opt-in: `ChatbotLiveTests`, bật `STREETBIZ_CHATBOT_LIVE_TEST=1`, chạy filter `FullyQualifiedName~ChatbotLiveTests`. Chỉ chạy trên local development có demo accounts và user-secrets hợp lệ. Tác vụ nhắc phí/retention được tháo khỏi test host. Test SQL không gọi AI thật; test provider chỉ gửi câu chào giả lập. Nó tạo rồi xóa nội dung hội thoại thử nghiệm, đăng nhập rồi đăng xuất phiên demo; không seed, reset hay quyết định nghiệp vụ. Không bật biến này trong CI mặc định.

`ChatbotPolicyTests` gồm 60 cặp role/câu hỏi tiếng Việt và 20 ca giả mạo role/tool, cùng schema/privacy cases. Đây là regression của policy/KB, **không phải** benchmark chất lượng LLM. Cần đánh giá câu trả lời với model thật, role fixtures và người phụ trách nghiệp vụ trước production.

Checklist thủ công trước phát hành: desktop/mobile/dark mode; focus/Escape/Shift+Enter; reduced-motion; mất mạng giữa stream; đổi tài khoản/role; hai tab cùng hội thoại; thu hồi phiên; chọn ảnh có consent; hủy/xóa trong lúc trả lời; phản hồi và liên kết chi tiết. Công cụ Browser của phiên triển khai gặp lỗi kết nối nên chưa ký nghiệm thu trực quan/E2E trình duyệt.

## Kết quả xác minh ngày 07/10/2026

- Backend: 752 test pass; hai smoke test opt-in được skip trong bộ offline/CI mặc định.
- SQL thật: test opt-in đã pass cho Vendor/Ward/Admin/Customer; bổ sung tài chính và hợp đồng vendor cũng pass. Chỉ nội dung hội thoại thử nghiệm được xóa và phiên demo được đăng xuất; không xóa dữ liệu nghiệp vụ.
- Groq: streaming câu chào giả lập thành công trong hai lần smoke test.
- Gemini (smoke test trước nâng cấp UI): lookup model `gemini-3.8-flash` HTTP 200, metadata xác nhận có GenerateContent; generation streaming timeout hai lần ở 45 giây, kiểm tra generation không streaming cũng timeout. Chưa thể kết luận nguyên nhân từ key/model hay dịch vụ/đường truyền. Chưa nghiệm thu Gemini/ảnh production. Nâng cấp UI đã bật ảnh ở development để thử nghiệm có consent; cấu hình production vẫn cần giữ tắt tới khi kiểm chứng provider. Runtime có deadline provider riêng và fallback văn bản trước khi phát delta đầu; không fallback ảnh sang text.
- Frontend: 497 test pass; build/typecheck pass, lint không có lỗi (ba warning Fast Refresh ở file có sẵn); có cảnh báo bundle lớn từ bản đồ/3D và annotation SignalR. Không mở rộng task để sửa các module đó.
- Browser trong môi trường báo lỗi metadata sandbox ngay khi kết nối; chưa có screenshot kiểm chứng desktop/mobile. Component tests không thay thế bước nghiệm thu trực quan này.
