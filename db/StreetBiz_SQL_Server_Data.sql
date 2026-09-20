/* ============================================================
   StreetBiz — reference data for db/StreetBiz_SQL_Server.sql

   The rows the application cannot function without, independent of any demo
   scenario: the role vocabulary, the administrative hierarchy every ward-scoped
   table hangs off, and the violation catalogue WARD-03 prices.

   Everything else -- pricing zones, slots, fee schedules, food categories --
   carries a created_by pointing at a real officer or administrator, so it cannot
   exist before accounts do and lives in docs/dev-seed-demo.sql instead.

   Idempotent: safe to run more than once. Only ever inserts what is missing.
   Run after db/StreetBiz_SQL_Server.sql. scripts/setup-local-db.ps1 does this.
   ============================================================ */

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ------------------------------------------------------------------
   Roles (AUTH). GUEST is deliberately absent -- see the schema comment:
   an unauthenticated visitor is an actor, never a row.
   ------------------------------------------------------------------ */
MERGE Roles AS target
USING (VALUES
    ('CUSTOMER',       N'Khách hàng'),
    ('VENDOR',         N'Hộ kinh doanh'),
    ('WARD_AUTHORITY', N'Cán bộ phường'),
    ('PLATFORM_ADMIN', N'Quản trị hệ thống')
) AS source (role_code, role_name)
ON target.role_code = source.role_code
WHEN NOT MATCHED THEN
    INSERT (role_code, role_name) VALUES (source.role_code, source.role_name);
GO

/* ------------------------------------------------------------------
   AdministrativeUnits.
   Ids are pinned with IDENTITY_INSERT because docs, seeds and
   scripts/e2e-auth-onboarding.sh refer to wards 10/11/12 by number.

   unit_type must be PROVINCE | DISTRICT | WARD -- CK_AdministrativeUnits_Type.
   The older docs/dev-seed.sql used 'CITY' for Đà Nẵng, which only ever worked
   because the EF-generated development schema carried no CHECK constraints.
   ------------------------------------------------------------------ */
IF NOT EXISTS (SELECT 1 FROM AdministrativeUnits)
BEGIN
    SET IDENTITY_INSERT AdministrativeUnits ON;

    INSERT INTO AdministrativeUnits (unit_id, unit_type, unit_name, parent_unit_id, contact_name, contact_phone) VALUES
        ( 1, 'PROVINCE', N'Thành phố Đà Nẵng',      NULL, NULL,                      NULL),

        ( 2, 'DISTRICT', N'Quận Hải Châu',             1, NULL,                      NULL),
        ( 3, 'DISTRICT', N'Quận Thanh Khê',            1, NULL,                      NULL),
        ( 4, 'DISTRICT', N'Quận Sơn Trà',              1, NULL,                      NULL),
        ( 5, 'DISTRICT', N'Quận Ngũ Hành Sơn',         1, NULL,                      NULL),

        (10, 'WARD',     N'Phường Hải Châu 1',         2, N'Nguyễn Thị Hồng Vân',    N'02363821021'),
        (11, 'WARD',     N'Phường Thanh Khê Đông',     3, N'Trần Quốc Bảo',          N'02363759112'),
        (12, 'WARD',     N'Phường An Hải Bắc',         4, N'Lê Thị Minh Thu',        N'02363944330'),
        (13, 'WARD',     N'Phường Nam Dương',          2, N'Phạm Văn Hải',           N'02363827445'),
        (14, 'WARD',     N'Phường Hòa Quý',            5, N'Võ Thị Kim Chi',         N'02363967208');

    SET IDENTITY_INSERT AdministrativeUnits OFF;
END;
GO

/* ------------------------------------------------------------------
   ViolationTypes (WARD-12 catalogue). Natural key: the code is what an
   officer picks on the spot and what PenaltyFeeSchedules prices.
   ------------------------------------------------------------------ */
MERGE ViolationTypes AS target
USING (VALUES
    ('NO_PERMIT',            N'Kinh doanh trên vỉa hè không có giấy phép',                 1),
    ('OUTSIDE_SLOT',         N'Bày bán vượt ra ngoài phạm vi ô được thuê',                 1),
    ('OUTSIDE_HOURS',        N'Kinh doanh ngoài khung giờ cho phép của khu vực',           1),
    ('BLOCK_PEDESTRIAN',     N'Cản trở lối đi bộ dành cho người đi đường',                 1),
    ('BLOCK_EMERGENCY',      N'Cản trở lối thoát hiểm, trụ nước chữa cháy hoặc trạm điện', 1),
    ('HYGIENE_VIOLATION',    N'Vi phạm quy định vệ sinh an toàn thực phẩm',                1),
    ('WASTE_DISPOSAL',       N'Xả rác, nước thải không đúng nơi quy định',                 1),
    ('NOISE_VIOLATION',      N'Gây tiếng ồn vượt mức cho phép',                            1),
    ('UNAUTHORIZED_TRANSFER', N'Tự ý sang nhượng ô cho người khác',                        1),
    ('PERMIT_TAMPERING',     N'Sử dụng giấy phép giả mạo hoặc đã bị thu hồi',              1)
) AS source (violation_type_code, description, is_active)
ON target.violation_type_code = source.violation_type_code
WHEN NOT MATCHED THEN
    INSERT (violation_type_code, description, is_active)
    VALUES (source.violation_type_code, source.description, source.is_active);
GO

SELECT
    (SELECT COUNT(*) FROM Roles)                                        AS [roles],
    (SELECT COUNT(*) FROM AdministrativeUnits WHERE unit_type='WARD')   AS [wards],
    (SELECT COUNT(*) FROM AdministrativeUnits)                          AS [admin_units],
    (SELECT COUNT(*) FROM ViolationTypes)                               AS [violation_types];
GO
