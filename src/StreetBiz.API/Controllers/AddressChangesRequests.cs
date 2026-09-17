namespace StreetBiz.API.Controllers;

public sealed record RequestAddressChangeRequest(
    long RegistrationId,
    string NewAddress,
    decimal? NewLatitude,
    decimal? NewLongitude,
    long? ReleasedContractId,
    long? RequestedNewSlotId);
