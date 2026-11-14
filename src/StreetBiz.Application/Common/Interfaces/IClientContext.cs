namespace StreetBiz.Application.Common.Interfaces;

/// <summary>Where the current request came from (null outside a web request, e.g. in a background job).</summary>
public interface IClientContext
{
    string? IpAddress { get; }
}
