# StreetBiz Voice — thiết kế và trạng thái triển khai

Trạng thái 09/10/2026: **đã triển khai theo phương án relay** (duyệt trong master prompt v2, `docs_system/CHATBOT_UPGRADE_V2_MASTER_PROMPT.md`). Người dùng đã thử một phiên thật trên development và xác nhận hội thoại hoạt động. Cấu hình, đo lường và checklist: [chatbot-runbook.md](chatbot-runbook.md#assistant-v2-09102026--giọng-nói-ảnh--món--quầy-giao-diện-ink--ember).

Đã có: vé phiên dùng một lần; WebSocket `/hubs/chatbot-voice/{id}` xác thực bằng JWT; relay `ChatbotVoiceService` ↔ `GeminiLiveVoiceProvider`; phụ đề hai chiều; ngắt lời theo generation; tool qua `ChatbotTools` với actor server; lưu phụ đề + thẻ dữ liệu mỗi lượt (không lưu audio); hạn mức phút/ngày trong DB; tự nối lại bằng session-resumption handle (tối đa `MaxReconnects`); tóm tắt phiên và metric độ trễ/token; chế độ dễ dùng.

Chưa có/chưa kiểm chứng: đo p95 trên điện thoại thật và bảng chi phí theo usage thật; Safari iOS; nối lại khi **trình duyệt** mất mạng (hiện kết thúc phiên, người dùng bấm nói lại); chạy nhiều API instance (vé và "một phiên/tài khoản" còn ở bộ nhớ).

Phần dưới là thiết kế gốc, vẫn là căn cứ cho các quyết định trên.

## Trải nghiệm

Người dùng bấm bắt đầu, cấp quyền mic và nói tự nhiên. Audio được truyền theo luồng trong một phiên; chatbot trả lời bằng audio, người dùng có thể ngắt lời. Phụ đề bật/tắt được. Cùng một hội thoại có thể tiếp tục bằng text sau khi kết thúc phiên audio; mic chỉ bật lại do thao tác chủ động.

UI là một vùng trò chuyện gồm tên trợ lý [AI], trạng thái bằng chữ, waveform từ mức audio thực, phụ đề tùy chọn, mute và kết thúc. Waveform chỉ là tín hiệu thu/phát, không biểu diễn độ chính xác. Thẻ số tiền, nguồn và thao tác vẫn xuất hiện ở hội thoại để đối chiếu.

Máy trạng thái:

`IDLE → REQUESTING_PERMISSION → CONNECTING → LISTENING ↔ SPEAKING → ENDING → ENDED`

`MUTED`, `RECONNECTING`, `ERROR` là trạng thái riêng có chỉ dẫn hành động. Bị từ chối mic không tự hỏi lại. Khi đang phát mà nhận interruption, hủy audio đã lên lịch, xóa hàng đợi và bỏ mọi chunk thuộc lượt cũ. Mute vẫn cho nghe câu trả lời nhưng ngừng gửi mic; end dừng cả track, AudioContext và socket.

## Phương án ưu tiên: relay qua backend

`Browser AudioWorklet ↔ WebSocket đã xác thực ↔ .NET voice relay ↔ Gemini Live`

- Giữ nguyên hệ auth hiện có. Lúc mở phiên và trước mỗi tool call phải kiểm tra phiên, actor và scope từ server.
- Gemini Live dùng model cấu hình riêng; kiểm tra account access, tiếng Việt, tool support và API version bằng tài liệu/model metadata tại thời điểm triển khai.
- Backend cấp session ID ngẫu nhiên, ràng buộc user/session/scope, có thời hạn và hạn mức. Không đưa API key dài hạn xuống frontend.
- Chỉ relay binary/audio và protocol events cần thiết, không nhận arbitrary provider config hoặc system prompt từ client.
- Công cụ nghiệp vụ dùng `ChatbotTools` đã được phân quyền; identity không đến từ model. Đặt giới hạn số calls, kết quả, concurrency; canceled calls không được phát kết quả muộn vào phiên mới.
- SignalR hiện có tiếp tục phục vụ card/status/snapshot nếu phù hợp. Không mặc định bọc mỗi gói PCM thành JSON SignalR.
- Dữ liệu đọc riêng tư phải thu hồi được khi logout/session revoke; kết thúc socket và xóa bộ đệm khi scope thay đổi.

Relay tăng lưu lượng và độ trễ qua backend nhưng cho phép kiểm soát phiên và chi phí. Prototype cần đo thực tế trước quyết định production.

Phương án thay thế: browser nối Gemini bằng ephemeral token do backend cấp, model/config bị giới hạn và token dùng ngắn hạn. Tool vẫn phải qua backend kiểm quyền. Cần giải quyết thu hồi phiên đang mở, kiểm soát tiêu thụ, và tránh coi tool response từ client là bằng chứng đáng tin. Không chọn phương án này chỉ vì ít code hơn.

## Audio và khôi phục

- HTTPS, thao tác người dùng trước khi mở mic/phát audio; xử lý autoplay policy trên Safari/mobile.
- Thu bằng getUserMedia và AudioWorklet, resample đúng codec/sample rate của model đã chọn. Không hard-code format của model cũ.
- Bật echo cancellation/noise suppression khi trình duyệt hỗ trợ; kiểm thử tai nghe và loa ngoài.
- Giới hạn send/playback buffer; phát audio theo clock của AudioContext, không nối file audio theo từng câu.
- Mỗi lượt có generation ID. Sau interruption, chỉ phát dữ liệu của lượt mới. Theo dõi phần đã phát để không coi toàn bộ câu bị ngắt là đã nghe.
- Mất mạng: dừng mic gửi, hiện trạng thái; không tích lũy audio riêng tư để gửi bù sau nhiều giây. Resume chỉ dùng handle hợp lệ, scope còn hiệu lực và ngữ cảnh cần thiết.
- Khi tab đóng/ẩn, thiết bị khóa hoặc audio bị hệ điều hành gián đoạn: ưu tiên tạm dừng; không cam kết background listening trên PWA.
- Từ chối audio input quá lớn, tốc độ gửi không hợp lệ và client không kết thúc phiên.

## Nguồn sự thật và quyền riêng tư

Lời nói của model là hỗ trợ AI. Số tiền, hồ sơ và giấy phép phải lấy từ công cụ live có quyền, đồng thời hiển thị thẻ dữ liệu gốc. Chưa đủ bằng chứng thì nói rõ cần kiểm tra, không tự kết luận. Mặc định không lưu audio thô trong StreetBiz; transcript nếu lưu phải theo chính sách lịch sử chatbot và có thông báo rõ. Chính sách lưu dữ liệu tại provider cần được xác minh theo loại tài khoản trước phát hành.

## Ngân sách đề xuất để duyệt

Prototype: một phiên đồng thời/tài khoản, tối đa 5 phút/phiên, cảnh báo rồi kết thúc sau thời gian không hoạt động được cấu hình. Đây là đề xuất cấu hình, chưa triển khai.

Chi phí = audio input tokens × đơn giá input + audio output tokens × đơn giá output + text/context/tool tokens × đơn giá tương ứng + băng thông/compute relay. Cần dùng usage thật: không suy ra chi phí chỉ từ thời lượng ghi âm, vì cách tính context và modality khác nhau theo model. Lập bảng kịch bản 100/1.000/10.000 phiên với thời lượng và tỷ lệ nói đã đo, dùng bảng giá chính thức tại ngày duyệt. Chưa chốt mức tiền khi chưa benchmark và chọn model.

Mục tiêu thử nghiệm, không phải cam kết: độ trễ p95 từ kết thúc ý đến audio đầu tiên ≤3 giây trên cấu hình thử; dừng playback trong 200ms sau khi client nhận interruption event. Đo riêng độ trễ từ người dùng bắt đầu ngắt lời đến lúc model gửi event. Tool lookup có thể kéo dài, UI cần báo tra cứu.

## Nghiệm thu trước bật

Test tiếng Việt với giọng miền Trung/tên riêng/ngày tháng/VND; tiếng ồn phố; im lặng; từ chối mic; revoke/logout; token/session hết hạn; mạng yếu; ngắt lời nhiều lần; audio đã buffer; phản hồi tool đến muộn; không thu/phát sau end. Có test không đọc chéo role, không nhận instructions trong audio để thay quyền. Chỉ bật cờ sau khi đo chất lượng, chi phí và latency trên thiết bị mục tiêu.

## Tài liệu tham khảo

- [Gemini Live overview](https://ai.google.dev/gemini-api/docs/live-api)
- [Interruption, VAD và audio capabilities](https://ai.google.dev/gemini-api/docs/live-api/capabilities)
- [Ephemeral tokens](https://ai.google.dev/gemini-api/docs/live-api/ephemeral-tokens)
- [Copilot voice UX](https://support.microsoft.com/en-us/microsoft-365-copilot/frequently-asked-questions-about-voice-features-in-microsoft-365-copilot?preview=true)
