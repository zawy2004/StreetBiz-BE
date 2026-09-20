namespace StreetBiz.Application.Common.Security;

/// <summary>Claims recovered from a verified permit QR payload.</summary>
public sealed record PermitTokenClaims(long ContractId, DateTime IssuedAtUtc);

/// <summary>
/// Signs and verifies the opaque token stored in DigitalPermits.qr_payload (SIDE-08). This is
/// deliberately not a JWT: a JWT's header+claims+signature wastes QR density, and permit
/// validity is derived from vw_PermitValidity, not from a token expiry claim. Signed over
/// contract_id + a random nonce rather than permit_id, so a token can be minted before the
/// IDENTITY-generated permit_id is known.
/// </summary>
public interface IPermitTokenService
{
    string Create(long contractId, DateTime issuedAtUtc);

    bool TryParse(string token, out PermitTokenClaims claims);
}
