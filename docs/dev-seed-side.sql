-- Sidewalk Slot & Rental (SIDE) fixture for local development.
--
-- SIDE-05..08, SIDE-12 and SIDE-13 all act on an ACTIVE rental contract, but a
-- contract is only ever created by WARD-08 (ward approves an application),
-- which does not exist yet. This script builds that fixture directly in the
-- database so the SIDE endpoints can be exercised end to end without it, and
-- is meant to become the acceptance fixture for WARD-08 once that module lands.
--
-- Idempotent: safe to run more than once. Builds on top of docs/dev-seed.sql
-- (roles + wards) and db/StreetBiz_SQL_Server_Data.sql (zones + slots), but
-- fills the gap itself if either is missing on a schema-only database.
--
-- Prerequisite: at least one BusinessRegistrations row, i.e. a vendor who has
-- registered through POST /api/auth/register and submitted
-- POST /api/vendor/registrations (see docs/testing-auth-vendor-onboarding.md).
-- This script does not create UserAccounts/Vendors -- passwords must go
-- through the app's BCrypt hasher, never be pasted into SQL.

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

-- 1) A ward-defined pricing zone (StreetBiz_SQL_Server_Data.sql already seeds
--    3 real zones; this block only fills the gap on a schema-only database).
IF NOT EXISTS (SELECT 1 FROM PricingZones)
BEGIN
    DECLARE @wardUnitId INT = (SELECT TOP 1 unit_id FROM AdministrativeUnits WHERE unit_type = 'WARD' ORDER BY unit_id);
    DECLARE @wardAuthorityUserId BIGINT = (SELECT TOP 1 user_id FROM UserAccounts WHERE role_code = 'WARD_AUTHORITY' ORDER BY user_id);

    IF @wardUnitId IS NULL OR @wardAuthorityUserId IS NULL
    BEGIN
        PRINT 'Missing a WARD administrative unit or a WARD_AUTHORITY account -- run docs/dev-seed.sql first, then re-run this script.';
        RETURN;
    END;

    INSERT INTO PricingZones (zone_name, ward_unit_id, price_per_day, created_by)
    VALUES (N'Dev seed zone', @wardUnitId, 20000, @wardAuthorityUserId);
END;

DECLARE @zoneId INT = (SELECT TOP 1 zone_id FROM PricingZones ORDER BY zone_id);

-- 2) A ward-defined slot in that zone (again, StreetBiz_SQL_Server_Data.sql
--    already seeds 7 real slots; this only fills the gap if it's missing).
IF NOT EXISTS (SELECT 1 FROM SidewalkSlots WHERE slot_code = 'DEV-SEED-01')
BEGIN
    INSERT INTO SidewalkSlots (slot_code, zone_id, latitude, longitude, slot_status, source)
    VALUES ('DEV-SEED-01', @zoneId, 16.0120, 108.2400, 'AVAILABLE', 'WARD_DEFINED');
END;

DECLARE @slotId BIGINT = (SELECT slot_id FROM SidewalkSlots WHERE slot_code = 'DEV-SEED-01');

-- 3) Pick a registration to hold the fixture contract: the first APPROVED one
--    if there already is one, otherwise promote the first registration found
--    (mirrors the manual-approval SQL in docs/testing-auth-vendor-onboarding.md).
DECLARE @registrationId BIGINT = (
    SELECT TOP 1 registration_id FROM BusinessRegistrations
    WHERE registration_status = 'APPROVED' ORDER BY registration_id
);

IF @registrationId IS NULL
BEGIN
    SET @registrationId = (SELECT TOP 1 registration_id FROM BusinessRegistrations ORDER BY registration_id);

    IF @registrationId IS NOT NULL
    BEGIN
        UPDATE BusinessRegistrations
        SET registration_status = 'APPROVED', reviewed_at = SYSUTCDATETIME()
        WHERE registration_id = @registrationId;
    END;
END;

IF @registrationId IS NULL
BEGIN
    PRINT 'No BusinessRegistrations row found -- register a vendor and submit a registration first (see docs/testing-auth-vendor-onboarding.md), then re-run this script.';
    RETURN;
END;

-- 4) An application + ACTIVE contract + issued permit for that registration,
--    unless it already holds an active one.
IF NOT EXISTS (
    SELECT 1 FROM RentalContracts c
    JOIN RentalApplications a ON a.application_id = c.application_id
    WHERE a.registration_id = @registrationId AND c.contract_status = 'ACTIVE'
)
BEGIN
    DECLARE @vendorId BIGINT = (SELECT vendor_id FROM BusinessRegistrations WHERE registration_id = @registrationId);

    INSERT INTO RentalApplications (registration_id, slot_id, application_method, application_status, requested_term_days)
    VALUES (@registrationId, @slotId, 'MANUAL_SELECTED', 'APPROVED', 90);
    DECLARE @applicationId BIGINT = SCOPE_IDENTITY();

    INSERT INTO RentalContracts (application_id, slot_id, vendor_id, start_date, end_date, contract_status)
    VALUES (@applicationId, @slotId, @vendorId, CAST(SYSUTCDATETIME() AS DATE), DATEADD(DAY, 90, CAST(SYSUTCDATETIME() AS DATE)), 'ACTIVE');
    DECLARE @contractId BIGINT = SCOPE_IDENTITY();

    UPDATE SidewalkSlots SET slot_status = 'ACTIVE' WHERE slot_id = @slotId;

    -- qr_payload here is a readable placeholder, not a real IPermitTokenService
    -- token -- WARD-08 will call IPermitTokenService.Create for that.
    INSERT INTO DigitalPermits (contract_id, qr_payload, permit_status)
    VALUES (@contractId, CONCAT('SEED-', @contractId, '-', FORMAT(SYSUTCDATETIME(), 'yyyyMMddHHmmss')), 'ACTIVE');
END;

SELECT
    (SELECT COUNT(*) FROM PricingZones) AS zones,
    (SELECT COUNT(*) FROM SidewalkSlots) AS slots,
    (SELECT COUNT(*) FROM BusinessRegistrations WHERE registration_status = 'APPROVED') AS approved_registrations,
    (SELECT COUNT(*) FROM RentalContracts WHERE contract_status = 'ACTIVE') AS active_contracts;
