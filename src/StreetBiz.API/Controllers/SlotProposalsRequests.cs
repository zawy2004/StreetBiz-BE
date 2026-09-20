namespace StreetBiz.API.Controllers;

public sealed record ProposeSlotRequest(
    long RegistrationId,
    int ZoneId,
    decimal Latitude,
    decimal Longitude,
    decimal? WidthMeters,
    decimal? LengthMeters,
    string ProposalPhotoUrl);
