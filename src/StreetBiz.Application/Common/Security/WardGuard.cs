using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Application.Common.Security;

/// <summary>
/// Rejects ward ids that are not WARD units. Without it an unknown id reaches the
/// (ward_unit_id, ward_unit_type) foreign key and surfaces as a 500.
/// </summary>
public static class WardGuard
{
    public static async Task EnsureWardAsync(
        this IAdministrativeUnitRepository units, int wardUnitId, CancellationToken cancellationToken)
    {
        if (!await units.IsWardAsync(wardUnitId, cancellationToken))
        {
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["WardUnitId"] = [AppMessages.InvalidWard],
            });
        }
    }
}
