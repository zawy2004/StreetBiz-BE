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
    /// <summary>CCCD mặt trước — id, họ tên, ngày sinh, giới tính, quốc tịch, địa chỉ thường trú.</summary>
    public const string IdentityDocument = "IDENTITY_DOCUMENT";

    /// <summary>CCCD mặt sau — dân tộc, ngày cấp, nơi cấp (không có ở mặt trước).</summary>
    public const string IdentityDocumentBack = "IDENTITY_DOCUMENT_BACK";

    /// <summary>Ảnh chân dung, đối chiếu với ảnh in trên CCCD (FPT.AI Facematch).</summary>
    public const string PortraitSelfie = "PORTRAIT_SELFIE";

    public const string BusinessLicense = "BUSINESS_LICENSE";
    public const string AddressProof = "ADDRESS_PROOF";
    public const string Other = "OTHER";

    public static readonly string[] All =
        [IdentityDocument, IdentityDocumentBack, PortraitSelfie, BusinessLicense, AddressProof, Other];
}

/// <summary>BusinessRegistrations.owner_gender values (Mẫu số 01 Phụ lục II, Thông tư 68/2025/TT-BTC).</summary>
public static class OwnerGenders
{
    public const string Male = "MALE";
    public const string Female = "FEMALE";
    public const string Other = "OTHER";
    public static readonly string[] All = [Male, Female, Other];
}

/// <summary>BusinessRegistrations.id_type values -- the legal document type backing owner identity.</summary>
public static class OwnerIdTypes
{
    /// <summary>Căn cước công dân gắn chip / Căn cước (Luật Căn cước 2023).</summary>
    public const string CitizenId = "CCCD";
    public const string Passport = "PASSPORT";
    public static readonly string[] All = [CitizenId, Passport];
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

    // ---- Mẫu số 01 Phụ lục II, Thông tư 68/2025/TT-BTC: chủ hộ kinh doanh ----
    public const string OwnerDateOfBirthRequired = "Vui lòng nhập ngày sinh của chủ hộ kinh doanh.";
    public const string OwnerGenderRequired = "Vui lòng chọn giới tính của chủ hộ kinh doanh.";
    public const string OwnerNationalityRequired = "Vui lòng nhập quốc tịch của chủ hộ kinh doanh.";
    public const string IdTypeRequired = "Vui lòng chọn loại giấy tờ pháp lý (CCCD hoặc hộ chiếu).";
    public const string IdIssuedDateRequired = "Vui lòng nhập ngày cấp giấy tờ pháp lý.";
    public const string IdIssuedPlaceRequired = "Vui lòng nhập nơi cấp giấy tờ pháp lý.";
    public const string PermanentAddressRequired = "Vui lòng nhập địa chỉ thường trú theo CCCD.";

    // ---- Ngành nghề, quy mô hộ kinh doanh ----
    public const string BusinessLineRequired = "Vui lòng nhập ngành, nghề kinh doanh.";
    public const string CapitalAmountRequired = "Vui lòng nhập vốn kinh doanh (VNĐ).";
    public const string LaborCountRequired = "Vui lòng nhập số lao động.";
    public const string PlannedStartDateRequired = "Vui lòng nhập ngày dự kiến bắt đầu hoạt động.";

    // ---- Cam kết ATTP / thành viên hộ gia đình ----
    public const string FoodSafetyCommitmentRequired = "Vui lòng xác nhận cam kết bảo đảm an toàn thực phẩm.";
    public const string HouseholdMemberNameRequired = "Vui lòng nhập họ tên đầy đủ của thành viên hộ gia đình.";

    // ---- Xác minh danh tính thủ công (KYC gate, BR-41) ----
    public const string IdentityVerificationRequiredForApproval =
        "Cán bộ phải xác nhận đã đối chiếu CCCD của chủ hộ kinh doanh trước khi duyệt hồ sơ. AI chỉ hỗ trợ trích xuất/đối chiếu, không thay thế xác minh của cán bộ.";
    public const string IdentityVerificationNoteRequired = "Vui lòng ghi chú ngắn gọn cách đối chiếu CCCD (ví dụ: đối chiếu trực tiếp tại UBND phường, ngày ...).";
}
