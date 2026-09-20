using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface ISidewalkZoneRepository
{
    Task<ZoneDetailRow?> GetDetailAsync(int zoneId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FeeComponentRow>> ListFeeComponentsAsync(int zoneId, CancellationToken cancellationToken);
}
