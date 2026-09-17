namespace StreetBiz.API.Controllers;

public sealed record RequestRenewalRequest(int RequestedTermDays);
public sealed record CancelContractRequest(string? Reason);
