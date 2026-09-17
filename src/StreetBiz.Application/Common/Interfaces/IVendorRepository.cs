namespace StreetBiz.Application.Common.Interfaces;

public interface IVendorRepository
{
    /// <summary>Resolves the vendor_id for a user account, or null if the user is not a vendor.</summary>
    Task<long?> GetVendorIdByUserAsync(long userId, CancellationToken cancellationToken);
}
