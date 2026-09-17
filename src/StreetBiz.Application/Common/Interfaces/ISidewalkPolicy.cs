namespace StreetBiz.Application.Common.Interfaces;

/// <summary>Configurable sidewalk-rental policy values.</summary>
public interface ISidewalkPolicy
{
    /// <summary>BR-11: the maximum distance a storefront-adjacent slot may be from the registration's declared address.</summary>
    double AdjacentRadiusMeters { get; }
}
