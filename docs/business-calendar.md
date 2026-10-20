# Lịch ngày làm việc (BusinessCalendar)

Các thời hạn tính theo **ngày làm việc** của cơ quan nhà nước dùng `BusinessCalendar`
(`src/StreetBiz.Application/Common/Models`):

- hạn giải trình vi phạm (Điều 61 Luật XLVPHC): 02 ngày làm việc nếu giải trình trực tiếp,
  05 ngày làm việc nếu bằng văn bản;
- hạn xử lý hồ sơ gia hạn (NĐ 241/2026 Điều 21): 3 ngày làm việc.

## Cách tính

- **Ngày bắt đầu không tính.** Thời hạn chạy từ ngày kế tiếp (Điều 147 Bộ luật Dân sự 2015,
  áp dụng cho xử lý vi phạm hành chính theo Điều 8 Luật XLVPHC).
- **Thời hạn hết vào cuối ngày cuối cùng** (Điều 148), tức 24:00 giờ Việt Nam, không phải
  đầu ngày đó. Hàm `EndOfDayUtc` trả về thời điểm này theo UTC.
- Ngày làm việc gồm thứ Hai đến thứ Sáu. Trừ ra các ngày nghỉ lễ và ngày nghỉ do hoán đổi;
  cộng thêm các thứ Bảy làm bù.

Ví dụ: biên bản lập thứ Hai 12/10/2026. Hạn giải trình trực tiếp là hết thứ Tư 14/10. Hạn
giải trình bằng văn bản là hết thứ Hai 19/10.

## Ngày nghỉ lễ

Ngày nghỉ Tết âm lịch, ngày hoán đổi và ngày làm bù do Bộ Nội vụ công bố từng năm, nên
không tự tính được. Danh sách nằm ở `VietnamPublicHolidays.cs`. Một ngày chỉ được thêm vào đó
khi đã có văn bản chính thức, hoặc khi được xác định theo Điều 112 Bộ luật Lao động 2019
(gồm quy tắc nghỉ bù khi ngày lễ trùng ngày nghỉ hằng tuần).

Hiện có: toàn bộ năm 2026 (Thông báo 9441/TB-BNV và thông báo Tết Dương lịch 2026), và năm
2027 gồm Tết Dương lịch, Tết Nguyên đán (04/02 đến 10/02), 30/4, nghỉ bù 1/5 và Quốc khánh
(02/09, 03/09).

**Chưa có, cần thêm khi được công bố:**

- Giỗ Tổ Hùng Vương 2027 (10/3 âm lịch);
- Ngày Văn hóa Việt Nam 24/11 (Nghị quyết 28/2026/QH16), cùng phương án nghỉ của từng năm.

Thiếu một ngày nghỉ thì thời hạn kết thúc sớm hơn thực tế, bất lợi cho người vi phạm. Vì vậy
lịch phải được cập nhật ngay khi có thông báo.

## Thêm ngày mà không cần phát hành lại

Trong cấu hình API (`appsettings.*.json` hoặc biến môi trường), rồi khởi động lại API:

```json
"BusinessCalendar": {
  "ExtraHolidays": ["2026-11-24"],
  "ExtraWorkingDays": []
}
```

`ExtraHolidays` là các ngày thường phải nghỉ; `ExtraWorkingDays` là các thứ Bảy phải đi làm
bù. Ở lần phát hành kế tiếp, chuyển các ngày này vào `VietnamPublicHolidays.cs` kèm tên văn
bản, rồi xóa khỏi cấu hình.
