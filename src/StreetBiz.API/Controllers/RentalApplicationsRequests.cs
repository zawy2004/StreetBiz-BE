namespace StreetBiz.API.Controllers;

public sealed record SubmitOpenSlotApplicationRequest(long RegistrationId, long SlotId, int RequestedTermDays);
public sealed record SubmitAdjacentApplicationRequest(long RegistrationId, long SlotId, int RequestedTermDays);
