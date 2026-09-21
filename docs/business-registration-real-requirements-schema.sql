/* ============================================================
   Business registration — align with real hộ kinh doanh procedure (2026-09)

   Context: BusinessRegistrations previously captured only display_name,
   declared_address, coordinates and id_number/biometric_consent_at (added by
   docs/legal-penalty-schema.sql). That is not enough to stand in for the real
   "Giấy đề nghị đăng ký hộ kinh doanh" (Mẫu số 01, Phụ lục II, Thông tư
   68/2025/TT-BTC, effective 01/07/2025, replacing the old Nghị định
   01/2021/NĐ-CP form) which StreetBiz's registration wizard is meant to
   collect data for. This script adds the legally required owner-identity,
   business-line and household-member fields, plus a separate food-safety
   commitment gate and a manual identity-verification gate.

   Adds:
     - BusinessRegistrations: owner identity fields (date of birth, gender,
       ethnicity, nationality, ID type/issue date/place, permanent + contact
       address) — required on Mẫu số 01 for the chủ hộ kinh doanh.
     - BusinessRegistrations: business_line/business_line_code (ngành, nghề
       kinh doanh), capital_amount (vốn kinh doanh), labor_count (số lao
       động), planned_start_date (ngày dự kiến bắt đầu hoạt động).
     - BusinessRegistrations: food_safety_commitment_at — separate, explicit
       self-declared commitment to food-safety conditions. Individual/
       household street-food vendors are exempt from the formal Giấy chứng
       nhận cơ sở đủ điều kiện ATTP (Nghị định 15/2018/NĐ-CP and successors),
       but must still commit to meeting food-safety conditions; this is that
       commitment record, not a certificate upload.
     - BusinessRegistrations: identity_verified_by/at/note — a Ward Authority
       officer's manual confirmation that they compared the vendor in person
       (or the uploaded photo) against the physical/chip CCCD. AI-OCR
       (id_number, AiComplianceService) is decision support only (BR-41) and
       is NOT a substitute for this: the current OCR path only extracts and
       self-compares a photo, it does not query the Bo Cong an/C06 national
       population database, so it cannot prove the document is genuine.
       DecideEnrollmentAsync must refuse an APPROVE decision while this is
       NULL.
     - BusinessRegistrationHouseholdMembers: 0..N household members who
       co-register the hộ kinh doanh and their capital contribution, per
       Mẫu số 01's "Thành viên hộ gia đình cùng góp vốn" section.

   NOT added (deliberately out of StreetBiz's scope):
     - Tax-method selection (thuế khoán/kê khai) — hộ kinh doanh tax method
       is the tax authority's concern (Nghị quyết 198/2025/QH15 phased out
       thuế khoán from 01/01/2026 anyway), not the Ward Authority's.
     - A food-safety *certificate* upload/number — not required for this
       vendor class; see food_safety_commitment_at above instead.

   Citations current as of 2026-09: Nghị định 168/2025/NĐ-CP (thay thế Nghị
   định 01/2021/NĐ-CP từ 01/07/2025), Thông tư 68/2025/TT-BTC (mẫu biểu),
   Nghị quyết 198/2025/QH15 (bỏ thuế khoán hộ kinh doanh từ 01/01/2026).

   Idempotent: every statement is guarded, so re-running is a no-op.
   Apply by hand (see docs/migration-guide.md -- the API never runs schema
   changes), then mirror into the canonical schema script.
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ---- Chủ hộ kinh doanh: owner identity (Mẫu số 01, mục chủ hộ) ---- */
IF COL_LENGTH('BusinessRegistrations', 'owner_date_of_birth') IS NULL
    ALTER TABLE BusinessRegistrations ADD owner_date_of_birth DATE NULL;
IF COL_LENGTH('BusinessRegistrations', 'owner_gender') IS NULL
    ALTER TABLE BusinessRegistrations ADD owner_gender NVARCHAR(10) NULL;
IF COL_LENGTH('BusinessRegistrations', 'owner_ethnicity') IS NULL
    ALTER TABLE BusinessRegistrations ADD owner_ethnicity NVARCHAR(50) NULL;
IF COL_LENGTH('BusinessRegistrations', 'owner_nationality') IS NULL
    ALTER TABLE BusinessRegistrations ADD owner_nationality NVARCHAR(50) NULL
        CONSTRAINT DF_BusinessRegistrations_owner_nationality DEFAULT (N'Việt Nam');
IF COL_LENGTH('BusinessRegistrations', 'id_type') IS NULL
    ALTER TABLE BusinessRegistrations ADD id_type NVARCHAR(20) NULL;
IF COL_LENGTH('BusinessRegistrations', 'id_issued_date') IS NULL
    ALTER TABLE BusinessRegistrations ADD id_issued_date DATE NULL;
IF COL_LENGTH('BusinessRegistrations', 'id_issued_place') IS NULL
    ALTER TABLE BusinessRegistrations ADD id_issued_place NVARCHAR(150) NULL;
IF COL_LENGTH('BusinessRegistrations', 'permanent_address') IS NULL
    ALTER TABLE BusinessRegistrations ADD permanent_address NVARCHAR(300) NULL;
IF COL_LENGTH('BusinessRegistrations', 'contact_address') IS NULL
    ALTER TABLE BusinessRegistrations ADD contact_address NVARCHAR(300) NULL;
GO

/* ---- Ngành nghề, quy mô hộ kinh doanh ---- */
IF COL_LENGTH('BusinessRegistrations', 'business_line') IS NULL
    ALTER TABLE BusinessRegistrations ADD business_line NVARCHAR(300) NULL;
IF COL_LENGTH('BusinessRegistrations', 'business_line_code') IS NULL
    ALTER TABLE BusinessRegistrations ADD business_line_code NVARCHAR(20) NULL;
IF COL_LENGTH('BusinessRegistrations', 'capital_amount') IS NULL
    ALTER TABLE BusinessRegistrations ADD capital_amount DECIMAL(18,0) NULL;
IF COL_LENGTH('BusinessRegistrations', 'labor_count') IS NULL
    ALTER TABLE BusinessRegistrations ADD labor_count INT NULL;
IF COL_LENGTH('BusinessRegistrations', 'planned_start_date') IS NULL
    ALTER TABLE BusinessRegistrations ADD planned_start_date DATE NULL;
GO

/* ---- Cam kết an toàn thực phẩm (tự khai, không phải giấy chứng nhận) ---- */
IF COL_LENGTH('BusinessRegistrations', 'food_safety_commitment_at') IS NULL
    ALTER TABLE BusinessRegistrations ADD food_safety_commitment_at DATETIME2 NULL;
GO

/* ---- Xác minh danh tính thủ công bởi cán bộ phường (KYC gate) ----
   AI-OCR (id_number) alone must never be treated as identity verification:
   it only reads and self-compares an uploaded photo, with no query against
   the Bo Cong an/CSDL quoc gia ve dan cu (C06). DecideEnrollmentAsync must
   require identity_verified_at before allowing an APPROVE decision. ---- */
IF COL_LENGTH('BusinessRegistrations', 'identity_verified_by') IS NULL
    ALTER TABLE BusinessRegistrations ADD identity_verified_by BIGINT NULL
        CONSTRAINT FK_BusinessRegistrations_IdentityVerifiedBy REFERENCES UserAccounts(user_id);
IF COL_LENGTH('BusinessRegistrations', 'identity_verified_at') IS NULL
    ALTER TABLE BusinessRegistrations ADD identity_verified_at DATETIME2 NULL;
IF COL_LENGTH('BusinessRegistrations', 'identity_verification_note') IS NULL
    ALTER TABLE BusinessRegistrations ADD identity_verification_note NVARCHAR(500) NULL;
GO

/* ---- Thành viên hộ gia đình cùng góp vốn (Mẫu số 01, mục thành viên) ---- */
IF OBJECT_ID('BusinessRegistrationHouseholdMembers', 'U') IS NULL
CREATE TABLE BusinessRegistrationHouseholdMembers (
    member_id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    registration_id        BIGINT NOT NULL
        CONSTRAINT FK_HouseholdMembers_Registration
        REFERENCES BusinessRegistrations(registration_id) ON DELETE CASCADE,
    full_name              NVARCHAR(150) NOT NULL,
    date_of_birth          DATE NULL,
    id_number              NVARCHAR(12) NULL,
    relationship_to_owner  NVARCHAR(50) NULL,
    capital_contribution   DECIMAL(18,0) NULL,
    created_at             DATETIME2 NOT NULL CONSTRAINT DF_HouseholdMembers_CreatedAt DEFAULT (SYSUTCDATETIME())
);
GO
