/* ============================================================
   Ward Review, Permit & Compliance schema (WARD-04..08, 11..13)

   Adds:
     - BusinessRegistrations.id_number: real citizen ID (CCCD), extracted by
       AI-OCR from the uploaded ID photo (AIC-01) -- NOT the phone number.
       A prior draft of this feature mistakenly compared the vendor's phone
       against "declared ID"; this column is the fix.
     - PenaltyFeeSchedules.legal_basis: the Nghi dinh/Dieu/Khoan citation for
       that ward's configured penalty_amount. AI never invents a legal
       citation -- it only classifies a violation_type and drafts wording
       FROM this column. No new table: PenaltyFeeSchedules already IS the
       per-ward, Ward-Authority-configured penalty table BR-33 requires;
       adding a citation column to it (rather than a parallel table) keeps
       one source of truth per (ward, violation_type).
     - ViolationTypes seed: the FK target of PenaltyFeeSchedules.violation_type
       was never seeded in any existing script.
     - PenaltyFeeSchedules seed: best-effort, per ward, only for wards that
       already have a WARD_AUTHORITY account (created_by is a real FK, not
       nullable). Wards without one yet are skipped with a printed notice --
       re-run this script after registering that ward's officer.

   Citations current as of 2026-09 (Nghi dinh 168/2024/ND-CP, 45/2022/ND-CP,
   144/2021/ND-CP -- NOT the superseded Nghi dinh 100/2019/123/2021, which a
   prior AI-generated draft of this feature incorrectly cited).

   Idempotent: every statement is guarded, so re-running is a no-op.
   Apply by hand (see docs/migration-guide.md -- the API never runs schema
   changes), then mirror into the canonical schema script.
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ---- BusinessRegistrations: real citizen ID, AI-OCR extracted ---- */
IF COL_LENGTH('BusinessRegistrations', 'id_number') IS NULL
    ALTER TABLE BusinessRegistrations ADD id_number NVARCHAR(12) NULL;
GO

/* ---- PenaltyFeeSchedules: attach the legal citation to the existing
   ward-configured rate row, instead of a parallel reference table ---- */
IF COL_LENGTH('PenaltyFeeSchedules', 'legal_basis') IS NULL
    ALTER TABLE PenaltyFeeSchedules ADD legal_basis NVARCHAR(500) NULL;
GO

/* ---- Penalties: dedicated column for the administrative sanction decision
   number. A prior draft stored this in waiver_reason, which is semantically
   wrong and would collide with a real future waiver on the same row. ---- */
IF COL_LENGTH('Penalties', 'decision_number') IS NULL
    ALTER TABLE Penalties ADD decision_number NVARCHAR(50) NULL;
GO

/* ---- Penalties: who signed the sanction decision. Chairman/Vice-Chairman
   signing authority (not the patrolling officer who only records the
   violation) is a real requirement under the Law on Handling of
   Administrative Violations -- self-declared text for now, not enforced via
   RBAC (see docs_system/features/ward-review-permit-compliance.md section 6.3
   for the deliberately-scoped-down decision). ---- */
IF COL_LENGTH('Penalties', 'signer_name') IS NULL
    ALTER TABLE Penalties ADD signer_name NVARCHAR(150) NULL;
IF COL_LENGTH('Penalties', 'signer_title') IS NULL
    ALTER TABLE Penalties ADD signer_title NVARCHAR(100) NULL;
GO

/* ---- BusinessRegistrations: separate, explicit consent to run AI-OCR on the
   uploaded CCCD photo (biometric data). Luat Bao ve du lieu ca nhan 2025 /
   Nghi dinh 356/2025/ND-CP requires biometric-data consent to be its own,
   unbundled affirmative action -- not folded into a general terms checkbox.
   ExtractIdDocumentAsync must refuse to run while this is NULL. ---- */
IF COL_LENGTH('BusinessRegistrations', 'biometric_consent_at') IS NULL
    ALTER TABLE BusinessRegistrations ADD biometric_consent_at DATETIME2 NULL;
GO

/* ---- ViolationTypes: seed the lookup table PenaltyFeeSchedules already
   has a (previously unseeded) FK to ---- */
IF NOT EXISTS (SELECT 1 FROM ViolationTypes WHERE violation_type_code = 'UNAUTHORIZED_BUSINESS_USE')
INSERT INTO ViolationTypes (violation_type_code, description, is_active) VALUES
    ('UNAUTHORIZED_BUSINESS_USE', N'Sử dụng trái phép lòng đường, vỉa hè để kinh doanh (không phép hoặc sai nội dung)', 1),
    ('EXPIRED_OR_INVALID_PERMIT', N'Giấy phép sử dụng tạm thời vỉa hè hết hiệu lực hoặc sử dụng sai nội dung giấy phép', 1),
    ('STREET_VENDING_RESTRICTED', N'Bán hàng rong tại tuyến phố cấm', 1),
    ('HYGIENE_LITTERING', N'Vứt, thải, để rác thải sinh hoạt trên vỉa hè, lòng đường', 1),
    ('OBSTRUCT_PUBLIC_ORDER', N'Đổ rác, vật cản gây mất an ninh trật tự công cộng', 1);
GO

/* ---- PenaltyFeeSchedules: per-ward seed, best-effort ----
   Runs once per ward that already has an active WARD_AUTHORITY account;
   silently does nothing for a ward that doesn't yet (no valid created_by). */
DECLARE @wardId INT, @officerId BIGINT;
DECLARE ward_cursor CURSOR LOCAL FOR
    SELECT unit_id FROM AdministrativeUnits WHERE unit_type = 'WARD';
OPEN ward_cursor;
FETCH NEXT FROM ward_cursor INTO @wardId;
WHILE @@FETCH_STATUS = 0
BEGIN
    SELECT TOP 1 @officerId = user_id FROM UserAccounts
        WHERE role_code = 'WARD_AUTHORITY' AND ward_unit_id = @wardId AND account_status = 'ACTIVE'
        ORDER BY user_id;

    IF @officerId IS NOT NULL
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM PenaltyFeeSchedules WHERE ward_unit_id = @wardId AND violation_type = 'UNAUTHORIZED_BUSINESS_USE' AND effective_to IS NULL)
            INSERT INTO PenaltyFeeSchedules (ward_unit_id, violation_type, penalty_amount, legal_basis, created_by) VALUES
                (@wardId, 'UNAUTHORIZED_BUSINESS_USE', 2500000, N'Nghị định 168/2024/NĐ-CP: sử dụng trái phép lòng đường, vỉa hè để kinh doanh dịch vụ ăn uống, bày bán hàng hóa (khung 2.000.000 - 3.000.000đ)', @officerId);

        IF NOT EXISTS (SELECT 1 FROM PenaltyFeeSchedules WHERE ward_unit_id = @wardId AND violation_type = 'EXPIRED_OR_INVALID_PERMIT' AND effective_to IS NULL)
            INSERT INTO PenaltyFeeSchedules (ward_unit_id, violation_type, penalty_amount, legal_basis, created_by) VALUES
                (@wardId, 'EXPIRED_OR_INVALID_PERMIT', 12500000, N'Nghị định 168/2024/NĐ-CP: sử dụng tạm thời lòng đường, vỉa hè không phép hoặc giấy phép hết giá trị/sai nội dung (khung 10.000.000 - 15.000.000đ)', @officerId);

        IF NOT EXISTS (SELECT 1 FROM PenaltyFeeSchedules WHERE ward_unit_id = @wardId AND violation_type = 'STREET_VENDING_RESTRICTED' AND effective_to IS NULL)
            INSERT INTO PenaltyFeeSchedules (ward_unit_id, violation_type, penalty_amount, legal_basis, created_by) VALUES
                (@wardId, 'STREET_VENDING_RESTRICTED', 225000, N'Nghị định 168/2024/NĐ-CP: bán hàng rong tại tuyến phố cấm (khung 200.000 - 250.000đ)', @officerId);

        IF NOT EXISTS (SELECT 1 FROM PenaltyFeeSchedules WHERE ward_unit_id = @wardId AND violation_type = 'HYGIENE_LITTERING' AND effective_to IS NULL)
            INSERT INTO PenaltyFeeSchedules (ward_unit_id, violation_type, penalty_amount, legal_basis, created_by) VALUES
                (@wardId, 'HYGIENE_LITTERING', 1500000, N'Nghị định 45/2022/NĐ-CP: vứt, thải, để rác thải sinh hoạt trên vỉa hè, lòng đường (khung 1.000.000 - 2.000.000đ)', @officerId);

        IF NOT EXISTS (SELECT 1 FROM PenaltyFeeSchedules WHERE ward_unit_id = @wardId AND violation_type = 'OBSTRUCT_PUBLIC_ORDER' AND effective_to IS NULL)
            INSERT INTO PenaltyFeeSchedules (ward_unit_id, violation_type, penalty_amount, legal_basis, created_by) VALUES
                (@wardId, 'OBSTRUCT_PUBLIC_ORDER', 4000000, N'Nghị định 144/2021/NĐ-CP: đổ rác, vật cản gây mất an ninh trật tự công cộng (khung 3.000.000 - 5.000.000đ)', @officerId);

        PRINT 'Seeded PenaltyFeeSchedules for ward ' + CAST(@wardId AS NVARCHAR(10));
    END
    ELSE
        PRINT 'Skipped ward ' + CAST(@wardId AS NVARCHAR(10)) + ': no active WARD_AUTHORITY account yet. Re-run after registering one.';

    FETCH NEXT FROM ward_cursor INTO @wardId;
END
CLOSE ward_cursor;
DEALLOCATE ward_cursor;
GO
