/* ============================================================
   Dev seed: Ward Review, Permit & Compliance test data

   Exercises the feature end to end without fabricating any real-looking
   biometric photo: WARD-04/05 (registration review), WARD-07/08 (permit
   issuance), WARD-11 (on-site inspection), WARD-12/13 (violation + sanction).

   Idempotent: safe to re-run. Builds on the accounts already created by
   dev-seed.sql (roles + wards) and whatever AUTH-01 registrations already
   exist -- run docs/legal-penalty-schema.sql BEFORE this script so
   PenaltyFeeSchedules/ViolationTypes are seeded (it needs a WARD_AUTHORITY
   account to already exist, which this script assumes is already there).

   Password reset to '123456' for the 3 seed accounts below, for convenience
   logging in through the UI -- BCrypt hash generated with the app's own
   PasswordHasher (work factor 12), not hand-rolled.
   ============================================================ */

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

DECLARE @hash NVARCHAR(100) = N'$2a$12$DGebbjCb1oH2mIs9BodM3OMK.EqAz5WxftPSG0sot2aYbbVEGdflq'; -- "123456"
UPDATE UserAccounts SET password_hash = @hash WHERE phone_number IN ('0905000004', '0905000002', '0905000001');

DECLARE @wardId INT = 10;          -- Phường Hải Châu 1
DECLARE @officerId BIGINT = (SELECT user_id FROM UserAccounts WHERE phone_number = '0905000004');
DECLARE @vendorUserId BIGINT = (SELECT user_id FROM UserAccounts WHERE phone_number = '0905000002');
DECLARE @vendorId BIGINT = (SELECT vendor_id FROM Vendors WHERE user_id = @vendorUserId);

IF @officerId IS NULL OR @vendorId IS NULL
BEGIN
    RAISERROR('Expected seed accounts (0905000004 ward officer, 0905000002 vendor) not found. Run dev-seed.sql and register these via AUTH-01 first.', 16, 1);
    RETURN;
END

/* ---- PricingZone for the pilot ward ---- */
IF NOT EXISTS (SELECT 1 FROM PricingZones WHERE ward_unit_id = @wardId AND zone_name = N'Khu vực Nguyễn Văn Linh')
    INSERT INTO PricingZones (ward_unit_id, zone_name, zone_code, price_per_day, created_by) VALUES
        (@wardId, N'Khu vực Nguyễn Văn Linh', 'NVL-01', 50000, @officerId);
DECLARE @zoneId INT = (SELECT TOP 1 zone_id FROM PricingZones WHERE ward_unit_id = @wardId AND zone_name = N'Khu vực Nguyễn Văn Linh');

/* ---- Two slots: one AVAILABLE for a fresh application test, one that will
   carry the seeded PENDING application below ---- */
IF NOT EXISTS (SELECT 1 FROM SidewalkSlots WHERE slot_code = 'NVL-T01')
    INSERT INTO SidewalkSlots (zone_id, slot_code, latitude, longitude, width_meters, length_meters, slot_status, source, has_power, has_water, has_trash_bin, business_category, created_at) VALUES
        (@zoneId, 'NVL-T01', 16.047100, 108.206800, 2.0, 3.0, 'AVAILABLE', 'WARD_DEFINED', 0, 0, 1, 'FOOD_BEVERAGE', SYSUTCDATETIME());
IF NOT EXISTS (SELECT 1 FROM SidewalkSlots WHERE slot_code = 'NVL-T02')
    INSERT INTO SidewalkSlots (zone_id, slot_code, latitude, longitude, width_meters, length_meters, slot_status, source, has_power, has_water, has_trash_bin, business_category, created_at) VALUES
        (@zoneId, 'NVL-T02', 16.047200, 108.206900, 2.0, 3.0, 'PENDING_APPLICATION', 'WARD_DEFINED', 1, 0, 1, 'RETAIL', SYSUTCDATETIME());
DECLARE @slotFreeId BIGINT = (SELECT slot_id FROM SidewalkSlots WHERE slot_code = 'NVL-T01');
DECLARE @slotPendingId BIGINT = (SELECT slot_id FROM SidewalkSlots WHERE slot_code = 'NVL-T02');

/* ---- Registration #1: SUBMITTED — for testing WARD-04/05 review + AI-OCR
   (no evidence attached yet; upload a real test photo through the UI to
   exercise the multimodal path -- this script never fabricates a face photo) ---- */
IF NOT EXISTS (SELECT 1 FROM BusinessRegistrations WHERE vendor_id = @vendorId AND display_name = N'Bánh Mì Cô Ba (Test)')
    INSERT INTO BusinessRegistrations (vendor_id, vendor_type, display_name, declared_address, address_latitude, address_longitude, ward_unit_id, registration_status, fast_track_flag, created_at) VALUES
        (@vendorId, 'ITINERANT', N'Bánh Mì Cô Ba (Test)', N'123 Nguyễn Văn Linh, Phường Hải Châu 1', 16.047150, 108.206850, @wardId, 'SUBMITTED', 0, SYSUTCDATETIME());
DECLARE @regSubmittedId BIGINT = (SELECT registration_id FROM BusinessRegistrations WHERE vendor_id = @vendorId AND display_name = N'Bánh Mì Cô Ba (Test)');

-- Placeholder evidence row: deliberately an unreachable URL, so opening this
-- registration's detail exercises the "fetch fails -> honest fallback,
-- IsAiGenerated:false" path safely. Replace with a real upload to test OCR.
IF NOT EXISTS (SELECT 1 FROM RegistrationEvidence WHERE registration_id = @regSubmittedId)
    INSERT INTO RegistrationEvidence (registration_id, evidence_type, file_url, uploaded_at) VALUES
        (@regSubmittedId, 'IDENTITY_DOCUMENT', 'https://example.invalid/test-id-placeholder.jpg', SYSUTCDATETIME());

/* ---- Registration #2: APPROVED — unblocks BR-16 so a rental application
   can be approved immediately without first clicking through review #1 ---- */
IF NOT EXISTS (SELECT 1 FROM BusinessRegistrations WHERE vendor_id = @vendorId AND display_name = N'Bún Bò Cô Ba (Test - Approved)')
    INSERT INTO BusinessRegistrations (vendor_id, vendor_type, display_name, declared_address, ward_unit_id, registration_status, reviewed_by, reviewed_at, review_decision_reason, fast_track_flag, created_at) VALUES
        (@vendorId, 'ITINERANT', N'Bún Bò Cô Ba (Test - Approved)', N'123 Nguyễn Văn Linh, Phường Hải Châu 1', @wardId, 'APPROVED', @officerId, SYSUTCDATETIME(), N'Đủ điều kiện trật tự đô thị (seed test)', 0, SYSUTCDATETIME());
DECLARE @regApprovedId BIGINT = (SELECT registration_id FROM BusinessRegistrations WHERE vendor_id = @vendorId AND display_name = N'Bún Bò Cô Ba (Test - Approved)');

/* ---- Rental Application: PENDING, on the approved registration — for
   testing WARD-07/08 (approve -> auto contract + permit + fee) ---- */
IF NOT EXISTS (SELECT 1 FROM RentalApplications WHERE registration_id = @regApprovedId AND slot_id = @slotPendingId)
    INSERT INTO RentalApplications (registration_id, slot_id, application_method, requested_term_days, application_status, created_at) VALUES
        (@regApprovedId, @slotPendingId, 'MANUAL_SELECTED', 30, 'PENDING', SYSUTCDATETIME());

PRINT 'Seeded: ward officer/vendor/customer passwords reset to 123456; zone ' + CAST(@zoneId AS NVARCHAR(10)) +
      '; slots NVL-T01 (free) / NVL-T02 (pending app); registration ' + CAST(@regSubmittedId AS NVARCHAR(10)) +
      ' (SUBMITTED, for WARD-04/05) and ' + CAST(@regApprovedId AS NVARCHAR(10)) + ' (APPROVED); rental application ready for WARD-07/08.';
GO
