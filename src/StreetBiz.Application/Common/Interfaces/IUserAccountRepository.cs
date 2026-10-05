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

    /// <summary>Counts a wrong password; once the limit is reached the account is locked, the counter reset and true returned.</summary>
    Task<bool> RecordFailedLoginAsync(long userId, int maxFailures, TimeSpan lockoutDuration, CancellationToken cancellationToken);

    Task ClearFailedLoginsAsync(long userId, CancellationToken cancellationToken);
}
