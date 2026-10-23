namespace StreetBiz.Application.Common.Security;

public static class PermitScanContexts
{
    public const string PublicCheck = "PUBLIC_CHECK";
}

public static class PermitScanResults
{
    public const string NotFound = "NOT_FOUND";
}

public static class VendorReportStatuses
{
    public const string Pending = "PENDING";
    public const string Reviewed = "REVIEWED";
    public const string Dismissed = "DISMISSED";
}

public static class CommunityMessages
{
    public const string VendorNotFound = "Active vendor not found.";
    public const string ReportReferenceMismatch = "The selected slot or permit does not belong to this vendor.";
}
