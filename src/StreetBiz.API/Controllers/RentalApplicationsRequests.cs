namespace StreetBiz.API.Controllers;

public sealed record SubmitOpenSlotApplicationRequest(
    long RegistrationId, long SlotId, int RequestedTermDays, bool CommitmentsAccepted);
public sealed record SubmitAdjacentApplicationRequest(long RegistrationId, long SlotId, int RequestedTermDays);
