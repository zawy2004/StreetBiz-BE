using StreetBiz.Application.DTOs.AdministrativeUnits;

namespace StreetBiz.Application.Common.Interfaces;

public interface IAdministrativeUnitRepository
{
    /// <summary>Wards available to attach an account or a registration to.</summary>
    Task<IReadOnlyList<WardDto>> ListWardsAsync(CancellationToken cancellationToken);

    /// <summary>True when the id is an AdministrativeUnits row of type WARD.</summary>
    Task<bool> IsWardAsync(int unitId, CancellationToken cancellationToken);
}
