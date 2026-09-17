using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Common;

public sealed class DateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}
