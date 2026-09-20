namespace StreetBiz.Application.Common.Security;

/// <summary>BusinessRegistrations.vendor_type values (DB CHECK).</summary>
public static class VendorTypes
{
    public const string FixedStorefront = "FIXED_STOREFRONT";
    public const string Itinerant = "ITINERANT";
    public static readonly string[] All = [FixedStorefront, Itinerant];
}

/// <summary>BusinessRegistrations.registration_status values (DB CHECK).</summary>
public static class RegistrationStatuses
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string UnderReview = "UNDER_REVIEW";
    public const string MoreInformationRequired = "MORE_INFORMATION_REQUIRED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Withdrawn = "WITHDRAWN";

    /// <summary>Statuses that count as an active, in-flight application (BR-09).</summary>
    public static readonly string[] ActivePending = [Submitted, UnderReview];

    /// <summary>Statuses a vendor may still edit (REG-04, BR-62).</summary>
    public static readonly string[] Editable = [Draft, Submitted, MoreInformationRequired];
}

/// <summary>RegistrationEvidence.evidence_type values (DB CHECK).</summary>
public static class EvidenceTypes
{
    public const string IdentityDocument = "IDENTITY_DOCUMENT";
    public const string BusinessLicense = "BUSINESS_LICENSE";
    public const string AddressProof = "ADDRESS_PROOF";
    public const string Other = "OTHER";
    public static readonly string[] All = [IdentityDocument, BusinessLicense, AddressProof, Other];
}

/// <summary>Messages for the Vendor Business Registration workflow (REG).</summary>
public static class RegMessages
{
    public const string Submitted = "Your business registration has been submitted successfully.";     // MSG09
    public const string SelectVendorType = "Please select a vendor type before continuing.";           // MSG10
    public const string DuplicatePending = "You already have a registration under review. Please wait for a decision or withdraw it first."; // BR-09
    public const string FixedNeedsAddress = "A fixed-storefront registration requires a business address."; // BR-07
    public const string UploadValidDocument = "Please upload a valid identity or business-licence document."; // MSG14
    public const string NotEditable = "This registration can no longer be edited because it is {0}.";  // MSG62
    public const string Withdrawn = "Your registration has been withdrawn.";
    public const string WithdrawBlockedActiveContract = "This registration cannot be withdrawn while it has an active rental contract.";
    public const string NotAVendor = "Only vendor accounts can manage business registrations.";
    public const string NotFound = "Business registration not found.";
}
