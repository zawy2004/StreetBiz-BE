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

-- 0) Pilot area: Đường Nguyễn Văn Linh, phường Nam Dương, quận Hải Châu.
--    StreetBiz_SQL_Server_Data.sql's own 7 slots are only approximately
--    placed inside Hoà Quý city blocks, not on any real street; these 20 sit
--    on the actual Nguyễn Văn Linh road centerline -- NVL-01..10 walk the
--    original 6 geocoded points' fitted axis at a 15 m pitch, and NVL-11..20
--    mirror them 38 m across to the far side of the road (confirmed against
--    OpenStreetMap: this is a divided one-way primary road -- near carriageway
--    ~14 m away, far carriageway ~20 m, building line beyond that -- so a
--    small offset would still land both rows on the same near side; a 4 m
--    pitch also made the 38 m offset wider than the along-street length,
--    which street-geometry.ts's straightness check rejects as "not a street"
--    -- 15 m keeps the segment properly elongated). NVL-1x faces NVL-0x
--    directly across the roadway, matching the street-strip
--    diagram's two-sided rendering to what's actually across the street.
IF NOT EXISTS (SELECT 1 FROM AdministrativeUnits WHERE unit_type = 'DISTRICT' AND unit_name = N'Quận Hải Châu')
BEGIN
    INSERT INTO AdministrativeUnits (unit_type, unit_name, parent_unit_id)
    VALUES ('DISTRICT', N'Quận Hải Châu', (SELECT TOP 1 unit_id FROM AdministrativeUnits WHERE unit_type = 'PROVINCE' ORDER BY unit_id));
END;

DECLARE @haiChauDistrictId INT = (SELECT unit_id FROM AdministrativeUnits WHERE unit_type = 'DISTRICT' AND unit_name = N'Quận Hải Châu');

IF NOT EXISTS (SELECT 1 FROM AdministrativeUnits WHERE unit_type = 'WARD' AND unit_name = N'Phường Nam Dương')
BEGIN
    INSERT INTO AdministrativeUnits (unit_type, unit_name, parent_unit_id)
    VALUES ('WARD', N'Phường Nam Dương', @haiChauDistrictId);
END;

DECLARE @namDuongWardId INT = (SELECT unit_id FROM AdministrativeUnits WHERE unit_type = 'WARD' AND unit_name = N'Phường Nam Dương');

IF NOT EXISTS (SELECT 1 FROM PricingZones WHERE zone_name = N'Đường Nguyễn Văn Linh')
BEGIN
    DECLARE @nvlWardAuthorityUserId BIGINT = (SELECT TOP 1 user_id FROM UserAccounts WHERE role_code = 'WARD_AUTHORITY' ORDER BY user_id);
    IF @nvlWardAuthorityUserId IS NOT NULL
    BEGIN
        INSERT INTO PricingZones (zone_name, ward_unit_id, price_per_day, available_from, available_to, created_by)
        VALUES (N'Đường Nguyễn Văn Linh', @namDuongWardId, 30000, '05:00', '22:00', @nvlWardAuthorityUserId);
    END;
END;

DECLARE @nvlZoneId INT = (SELECT zone_id FROM PricingZones WHERE zone_name = N'Đường Nguyễn Văn Linh');

IF @nvlZoneId IS NOT NULL
BEGIN
    -- Reposition all 20 -- NVL-01..10 keep the original fitted-axis lattice,
    -- NVL-11..20 move to the real far side of the road. UPDATE rather than
    -- DELETE + re-INSERT -- slot_id is an FK target for
    -- RentalApplications/RentalContracts, so a vendor who already applied for
    -- (or holds a contract on) one of these slots would break the script.
    -- Geometry only, never slot_status: re-running must not erase a status a
    -- developer set by using the app.
    UPDATE s
    SET latitude = v.latitude, longitude = v.longitude,
        width_meters = v.width_meters, length_meters = v.length_meters
    FROM SidewalkSlots s
    JOIN (VALUES
        ('NVL-01', 16.060429, 108.213724, 1.80, 2.50),
        ('NVL-02', 16.060455, 108.213861, 2.00, 3.00),
        ('NVL-03', 16.060480, 108.213999, 2.50, 3.20),
        ('NVL-04', 16.060506, 108.214137, 2.00, 4.00),
        ('NVL-05', 16.060532, 108.214274, 1.80, 2.50),
        ('NVL-06', 16.060558, 108.214412, 2.00, 3.00),
        ('NVL-07', 16.060584, 108.214550, 2.50, 3.20),
        ('NVL-08', 16.060610, 108.214687, 2.00, 4.00),
        ('NVL-09', 16.060636, 108.214825, 1.80, 2.50),
        ('NVL-10', 16.060661, 108.214963, 2.00, 3.00),
        -- 38 m across the road from the matching NVL-0x above (see block comment).
        ('NVL-11', 16.060091, 108.213791, 2.50, 3.20),
        ('NVL-12', 16.060117, 108.213929, 2.00, 4.00),
        ('NVL-13', 16.060143, 108.214067, 1.80, 2.50),
        ('NVL-14', 16.060169, 108.214204, 2.00, 3.00),
        ('NVL-15', 16.060195, 108.214342, 2.50, 3.20),
        ('NVL-16', 16.060221, 108.214480, 2.00, 4.00),
        ('NVL-17', 16.060246, 108.214617, 1.80, 2.50),
        ('NVL-18', 16.060272, 108.214755, NULL,  NULL),
        ('NVL-19', 16.060298, 108.214893, 2.50, 3.20),
        ('NVL-20', 16.060324, 108.215030, 2.00, 4.00)
    ) AS v(slot_code, latitude, longitude, width_meters, length_meters)
      ON s.slot_code = v.slot_code
    WHERE s.source = 'WARD_DEFINED';  -- never touch a vendor proposal

    -- Same 20 rows, for a schema-only database that has none of them yet. A
    -- few carry non-default statuses (set here, at INSERT time, so a re-run
    -- leaves them alone too) so the diagram has taken/pending/suspended slots
    -- to draw, and NVL-18 deliberately has no recorded size to exercise the
    -- "chưa đo" rendering.
    INSERT INTO SidewalkSlots (slot_code, zone_id, latitude, longitude, width_meters, length_meters, slot_status, source)
    SELECT v.slot_code, @nvlZoneId, v.latitude, v.longitude, v.width_meters, v.length_meters, v.slot_status, 'WARD_DEFINED'
    FROM (VALUES
        ('NVL-01', 16.060429, 108.213724, 1.80,  2.50, 'AVAILABLE'),
        ('NVL-02', 16.060455, 108.213861, 2.00,  3.00, 'AVAILABLE'),
        ('NVL-03', 16.060480, 108.213999, 2.50,  3.20, 'AVAILABLE'),
        ('NVL-04', 16.060506, 108.214137, 2.00,  4.00, 'AVAILABLE'),
        ('NVL-05', 16.060532, 108.214274, 1.80,  2.50, 'AVAILABLE'),
        ('NVL-06', 16.060558, 108.214412, 2.00,  3.00, 'AVAILABLE'),
        ('NVL-07', 16.060584, 108.214550, 2.50,  3.20, 'AVAILABLE'),
        ('NVL-08', 16.060610, 108.214687, 2.00,  4.00, 'AVAILABLE'),
        ('NVL-09', 16.060636, 108.214825, 1.80,  2.50, 'ACTIVE'),
        ('NVL-10', 16.060661, 108.214963, 2.00,  3.00, 'AVAILABLE'),
        ('NVL-11', 16.060091, 108.213791, 2.50,  3.20, 'AVAILABLE'),
        ('NVL-12', 16.060117, 108.213929, 2.00,  4.00, 'PENDING_APPLICATION'),
        ('NVL-13', 16.060143, 108.214067, 1.80,  2.50, 'ACTIVE'),
        ('NVL-14', 16.060169, 108.214204, 2.00,  3.00, 'AVAILABLE'),
        ('NVL-15', 16.060195, 108.214342, 2.50,  3.20, 'AVAILABLE'),
        ('NVL-16', 16.060221, 108.214480, 2.00,  4.00, 'SUSPENDED'),
        ('NVL-17', 16.060246, 108.214617, 1.80,  2.50, 'AVAILABLE'),
        ('NVL-18', 16.060272, 108.214755, NULL,  NULL, 'AVAILABLE'),
        ('NVL-19', 16.060298, 108.214893, 2.50,  3.20, 'AVAILABLE'),
        ('NVL-20', 16.060324, 108.215030, 2.00,  4.00, 'AVAILABLE')
    ) AS v(slot_code, latitude, longitude, width_meters, length_meters, slot_status)
    WHERE NOT EXISTS (SELECT 1 FROM SidewalkSlots s WHERE s.slot_code = v.slot_code);
END;

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

-- Avoid the Nguyễn Văn Linh pilot zone here: on a schema-only database it is
-- the first zone created (block 0 above), and DEV-SEED-01 below sits 6+ km
-- away from it -- landing it in that zone would make it an outlier that
-- throws off the street-strip diagram's axis fit for every NVL-* slot.
DECLARE @zoneId INT = (
    SELECT TOP 1 zone_id FROM PricingZones WHERE zone_name <> N'Đường Nguyễn Văn Linh' ORDER BY zone_id
);
IF @zoneId IS NULL SET @zoneId = (SELECT TOP 1 zone_id FROM PricingZones ORDER BY zone_id);

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
