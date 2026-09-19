namespace StreetBiz.Application.Common.Interfaces;

/// <summary>Ambient information about the authenticated caller, resolved from the JWT.</summary>
public interface ICurrentUser
{
    long? UserId { get; }
    long? SessionId { get; }
    string? RoleCode { get; }
    bool IsAuthenticated { get; }
}
