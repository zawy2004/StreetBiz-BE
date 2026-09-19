using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.DTOs.VendorRegistration;

namespace StreetBiz.Application.Common.Security;

/// <summary>Resolves the current caller's vendor_id and guards registration ownership.</summary>
public interface IVendorContext
{
    Task<long> RequireVendorIdAsync(CancellationToken cancellationToken);
    Task<BizRegistration> RequireOwnedRegistrationAsync(long registrationId, CancellationToken cancellationToken);
}

public sealed class VendorContext(
    ICurrentUser currentUser,
    IUserAccountRepository users,
    IVendorRepository vendorRepository,
    IBusinessRegistrationRepository registrationRepository) : IVendorContext
{
    public async Task<long> RequireVendorIdAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new AuthenticationException("No active session.");

        if (currentUser.RoleCode != RoleCodes.Vendor)
        {
            throw new ForbiddenException(RegMessages.NotAVendor);
        }

        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null
            || user.RoleCode != RoleCodes.Vendor
            || user.AccountStatus != AccountStatuses.Active)
        {
            throw new ForbiddenException(RegMessages.NotAVendor);
        }

        return await vendorRepository.GetVendorIdByUserAsync(userId, cancellationToken)
            ?? throw new ForbiddenException(RegMessages.NotAVendor);
    }

    public async Task<BizRegistration> RequireOwnedRegistrationAsync(
        long registrationId, CancellationToken cancellationToken)
    {
        var vendorId = await RequireVendorIdAsync(cancellationToken);

        var registration = await registrationRepository.GetByIdAsync(registrationId, cancellationToken)
            ?? throw new NotFoundException(RegMessages.NotFound);

        if (registration.VendorId != vendorId)
        {
            throw new ForbiddenException(AppMessages.Forbidden);
        }

        return registration;
    }
}

/// <summary>Maps domain records to DTOs for the Vendor Registration API.</summary>
public static class RegistrationMapper
{
    public static BusinessRegistrationDto ToDto(this BizRegistration r) => new(
        r.RegistrationId, r.VendorType, r.DisplayName, r.DeclaredAddress,
        r.AddressLatitude, r.AddressLongitude, r.WardUnitId,
        r.RegistrationStatus, r.FastTrackFlag, r.ReviewDecisionReason, r.ReviewedAt,
        r.CreatedAt, r.UpdatedAt);

    public static RegistrationEvidenceDto ToDto(this BizRegistrationEvidence e) => new(
        e.EvidenceId, e.RegistrationId, e.EvidenceType, e.FileUrl, e.UploadedAt);
}
