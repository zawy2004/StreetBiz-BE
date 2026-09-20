namespace StreetBiz.API.Controllers;

public sealed record RequestTransferRequest(long ContractId, string ToVendorPhone);
