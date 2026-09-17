namespace StreetBiz.Application.Common.Interfaces;

public interface IRentalContractRepository
{
    /// <summary>BR-12: true when the registration already holds an ACTIVE storefront-adjacent contract.</summary>
    Task<bool> HasActiveAdjacentContractAsync(long registrationId, CancellationToken cancellationToken);
}
