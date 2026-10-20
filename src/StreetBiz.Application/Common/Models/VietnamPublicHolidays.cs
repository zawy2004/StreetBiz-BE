namespace StreetBiz.Application.Common.Models;

/// <summary>
/// Weekdays off and Saturdays worked by state offices (cán bộ, công chức, viên chức), which is
/// the calendar a ward follows. Tết and the days swapped around a holiday are announced by the
/// Ministry of Home Affairs each year, so they are listed per year rather than computed. A date
/// only goes in here once it is fixed by Điều 112 Bộ luật Lao động 2019 (including its "nghỉ bù"
/// rule when a holiday falls on a weekend) or officially announced. Giỗ Tổ 2027 and Ngày Văn hóa
/// Việt Nam (24/11) are not announced yet: they go in the <c>BusinessCalendar</c> configuration
/// section once they are, until the next release adds them here.
/// </summary>
public static class VietnamPublicHolidays
{
    /// <summary>Weekdays on which ward offices are closed.</summary>
    public static readonly IReadOnlyList<DateOnly> DaysOff =
    [
        // 2026
        new(2026, 1, 1),   // Tết Dương lịch
        new(2026, 1, 2),   // swapped with Saturday 10/01
        new(2026, 2, 16),  // Tết Nguyên đán Bính Ngọ, 16/02 to 20/02 (Thông báo 9441/TB-BNV)
        new(2026, 2, 17),
        new(2026, 2, 18),
        new(2026, 2, 19),
        new(2026, 2, 20),
        new(2026, 4, 27),  // Giỗ Tổ Hùng Vương falls on Sunday 26/04: next working day off
        new(2026, 4, 30),  // Ngày Chiến thắng
        new(2026, 5, 1),   // Quốc tế Lao động
        new(2026, 8, 31),  // swapped with Saturday 22/08 (Thông báo 9441/TB-BNV)
        new(2026, 9, 1),   // Quốc khánh, 01/09 and 02/09
        new(2026, 9, 2),

        // 2027
        new(2027, 1, 1),   // Tết Dương lịch
        new(2027, 2, 4),   // Tết Nguyên đán Đinh Mùi, 04/02 to 10/02 (Bộ Nội vụ)
        new(2027, 2, 5),
        new(2027, 2, 8),
        new(2027, 2, 9),
        new(2027, 2, 10),
        new(2027, 4, 30),  // Ngày Chiến thắng
        new(2027, 5, 3),   // Quốc tế Lao động falls on Saturday 01/05: next working day off
        new(2027, 9, 2),   // Quốc khánh, 02/09 and 03/09 (Bộ Nội vụ)
        new(2027, 9, 3),
    ];

    /// <summary>Saturdays worked in exchange for a weekday taken off next to a holiday (làm bù).</summary>
    public static readonly IReadOnlyList<DateOnly> WorkingSaturdays =
    [
        new(2026, 1, 10),
        new(2026, 8, 22),
    ];
}
