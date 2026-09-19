namespace StreetBiz.Application.Common.Interfaces;

/// <summary>Configurable sidewalk-rental policy values.</summary>
public interface ISidewalkPolicy
{
    /// <summary>BR-11: the maximum distance a storefront-adjacent slot may be from the registration's declared address.</summary>
    double AdjacentRadiusMeters { get; }

    /// <summary>Minutes a slot hold lasts before it lapses.</summary>
    int SlotHoldTtlMinutes { get; }

    /// <summary>Maximum number of live holds a single registration may own.</summary>
    int MaxSlotHoldsPerRegistration { get; }
}
