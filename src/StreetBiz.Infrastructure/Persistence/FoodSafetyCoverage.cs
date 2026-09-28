using System.Linq.Expressions;
using StreetBiz.Application.Features.FoodSafety;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence;

/// <summary>
/// The single answer to "may this dish be sold as far as ATTP is concerned". A dish in a
/// high-risk category needs an APPROVED food-safety application, still in date, that lists it.
/// Derived on every read, so an expired certificate takes the dish off sale without a job.
/// </summary>
public static class FoodSafetyCoverage
{
    public static Expression<Func<MenuItem, bool>> Sellable(DateOnly today) => item =>
        !item.category.requires_food_safety
        || item.FoodSafetyApplicationItems.Any(covered =>
            covered.application.application_status == FoodSafetyStatuses.Approved
            && covered.application.expires_on >= today);

    public static Expression<Func<MenuItem, bool>> Certified(DateOnly today) => item =>
        item.FoodSafetyApplicationItems.Any(covered =>
            covered.application.application_status == FoodSafetyStatuses.Approved
            && covered.application.expires_on >= today);
}
