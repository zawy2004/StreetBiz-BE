-- Reference data required by Authentication and Vendor Onboarding in a LOCAL
-- development database. Idempotent: safe to run more than once.
--
--   * Roles: UserAccounts.role_code is a foreign key to Roles.
--   * AdministrativeUnits: registrations need a unit with unit_type = 'WARD'
--     (FK on ward_unit_id + computed ward_unit_type = 'WARD').
--
-- Do not run against the shared/production StreetBizDB; its reference data is
-- owned by the database team (see docs/migration-guide.md).

SET NOCOUNT ON;

MERGE Roles AS target
USING (VALUES
    ('CUSTOMER',       N'Customer'),
    ('VENDOR',         N'Vendor'),
    ('WARD_AUTHORITY', N'Ward Authority'),
    ('PLATFORM_ADMIN', N'Platform Administrator')
) AS source (role_code, role_name)
ON target.role_code = source.role_code
WHEN NOT MATCHED THEN
    INSERT (role_code, role_name) VALUES (source.role_code, source.role_name);

IF NOT EXISTS (SELECT 1 FROM AdministrativeUnits WHERE unit_id IN (1, 10, 11, 12))
BEGIN
    SET IDENTITY_INSERT AdministrativeUnits ON;

    INSERT INTO AdministrativeUnits (unit_id, unit_type, unit_name, parent_unit_id) VALUES
        (1,  'CITY', N'Thành phố Đà Nẵng',     NULL),
        (10, 'WARD', N'Phường Hải Châu 1',     1),
        (11, 'WARD', N'Phường Thanh Khê Đông', 1),
        (12, 'WARD', N'Phường An Hải Bắc',     1);

    SET IDENTITY_INSERT AdministrativeUnits OFF;
END;

SELECT
    (SELECT COUNT(*) FROM Roles) AS roles,
    (SELECT COUNT(*) FROM AdministrativeUnits WHERE unit_type = 'WARD') AS wards;
