using StreetBiz.Application.Common.Geo;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>How far from a stall a customer may be when placing a pickup order. Configured, not per-ward.</summary>
public interface IPickupRangePolicy
{
    /// <summary>When false, checkout accepts an order from anywhere and asks for no position.</summary>
    bool Enforced { get; }

    PickupRangeLimits Limits { get; }
}
