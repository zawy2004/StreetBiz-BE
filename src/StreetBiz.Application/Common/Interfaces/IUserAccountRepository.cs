using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface IUserAccountRepository
{
    Task<bool> PhoneExistsAsync(string phoneNumber, CancellationToken cancellationToken);
    Task<AppUser?> GetByPhoneAsync(string phoneNumber, CancellationToken cancellationToken);
    Task<AppUser?> GetByIdAsync(long userId, CancellationToken cancellationToken);

    /// <summary>Creates the account and, when role = VENDOR, the linked Vendor row. Returns the new user id.</summary>
    Task<long> CreateAsync(NewUser user, CancellationToken cancellationToken);

    Task UpdatePasswordHashAsync(long userId, string passwordHash, CancellationToken cancellationToken);
}
