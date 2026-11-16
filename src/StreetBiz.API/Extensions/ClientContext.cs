using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.API.Extensions;

/// <summary>The caller's address as ASP.NET sees it (already corrected by forwarded headers behind a trusted proxy).</summary>
public sealed class ClientContext(IHttpContextAccessor httpContextAccessor) : IClientContext
{
    public string? IpAddress => httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}
