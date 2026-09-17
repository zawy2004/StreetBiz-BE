namespace StreetBiz.Application.DTOs.AdministrativeUnits;

/// <summary>A selectable ward for the registration / sign-up ward pickers.</summary>
public sealed record WardDto(int UnitId, string UnitName, string? ParentName);
