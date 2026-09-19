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

    /// <summary>
    /// BR-07: a fixed storefront must prove its business licence, an itinerant vendor
    /// only its identity. Mirrors `requiredEvidence()` in the SPA wizard so the rule is
    /// enforced server-side too (CR-04), not just in the browser.
    /// </summary>
    public static string[] RequiredFor(string vendorType) =>
        vendorType == VendorTypes.FixedStorefront
            ? [IdentityDocument, BusinessLicense]
            : [IdentityDocument];

    /// <summary>Vietnamese labels, matching EVIDENCE_LABELS in the SPA.</summary>
    public static string Label(string evidenceType) => evidenceType switch
    {
        IdentityDocument => "CCCD gắn chip",
        BusinessLicense => "Giấy phép kinh doanh",
        AddressProof => "Giấy tờ địa chỉ",
        _ => "Giấy tờ khác",
    };
}

/// <summary>Messages for the Vendor Business Registration workflow (REG), in Vietnamese.</summary>
public static class RegMessages
{
    public const string Submitted = "Đã nộp hồ sơ đăng ký kinh doanh.";                                // MSG09
    public const string SelectVendorType = "Vui lòng chọn loại hình kinh doanh.";                      // MSG10
    public const string DuplicatePending = "Bạn đang có một hồ sơ chờ xét duyệt. Vui lòng chờ kết quả hoặc rút hồ sơ đó trước."; // BR-09
    public const string FixedNeedsAddress = "Cửa hàng cố định cần nhập địa chỉ kinh doanh.";           // BR-07
    public const string UploadValidDocument = "Vui lòng tải lên giấy tờ tuỳ thân hoặc giấy phép kinh doanh hợp lệ."; // MSG14
    public const string NotEditable = "Hồ sơ không thể chỉnh sửa vì {0}.";                             // MSG62
    public const string Withdrawn = "Đã rút hồ sơ đăng ký.";
    public const string WithdrawBlockedActiveContract = "Không thể rút hồ sơ khi đang có hợp đồng thuê ô vỉa hè hiệu lực.";
    public const string NotAVendor = "Chỉ tài khoản Hộ kinh doanh mới quản lý được hồ sơ đăng ký.";
    public const string NotFound = "Không tìm thấy hồ sơ đăng ký.";
    public const string DisplayNameRequired = "Vui lòng nhập tên hộ kinh doanh.";
    public const string DisplayNameTooLong = "Tên hộ kinh doanh tối đa 180 ký tự.";

    /// <summary>Vietnamese wording for the status inserted into <see cref="NotEditable"/>.</summary>
    public static string StatusWord(string status) => status switch
    {
        RegistrationStatuses.Approved => "đã được duyệt",
        RegistrationStatuses.Rejected => "đã bị từ chối",
        RegistrationStatuses.Withdrawn => "đã được rút",
        RegistrationStatuses.UnderReview => "đang được xét duyệt",
        _ => status,
    };
}
