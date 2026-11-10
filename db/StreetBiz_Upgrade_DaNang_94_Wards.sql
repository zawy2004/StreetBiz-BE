/* ============================================================
   StreetBiz — one-off upgrade: Đà Nẵng's 94 commune-level units
   ------------------------------------------------------------
   Only for a database built before October 2026, whose ward list
   still shows the 5 pre-2025 wards (Phường Hải Châu 1, Thanh Khê
   Đông, An Hải Bắc, Nam Dương, Hòa Quý) under 4 districts. A database
   built from the current db/StreetBiz_SQL_Server.sql already has the
   94 units: running this on it changes nothing. Rebuilding with
   scripts/setup-local-db.ps1 -Recreate reaches the same result but
   drops every row; this keeps them.

   Leaves AdministrativeUnits exactly as section 10 of
   db/StreetBiz_SQL_Server.sql: 23 phường, 70 xã and Đặc khu Hoàng Sa
   (Nghị quyết 1659/NQ-UBTVQH15, from 1/7/2025), all unit_type WARD
   directly under unit 1 (Thành phố Đà Nẵng); no district level.
   Wards 10/11/12/14 keep their ids, contacts and every row pointing
   at them, under the names of the phường they are part of now:
   Hải Châu, Thanh Khê, An Hải, Ngũ Hành Sơn. Ward 13 (Nam Dương,
   merged into Phước Ninh by NQ 1251, now phường Hải Châu) and the
   districts are removed.

   One transaction: on any error nothing changes. It stops, changing
   nothing, if a ward it would remove is still used by an account,
   registration, pricing zone, penalty rate or compliance policy:
   moving those is a ward decision, not a script's.

   Run it, then, on a development database with the demo seed,
   StreetBiz_Upgrade_Demo_Ward_Names.sql:
     sqlcmd -S "(localdb)\MSSQLLocalDB" -d StreetBizDB -E -I -f 65001 -i db\StreetBiz_Upgrade_DaNang_94_Wards.sql
   (Docker: -S "localhost,1433" -U sa -P <password> in place of -S … -E.)
   -f 65001 makes sqlcmd read this file as UTF-8; SSMS and Azure Data
   Studio do that by themselves. Safe to run twice.
   ============================================================ */
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;   -- the filtered indexes on the tables below need it
SET XACT_ABORT ON;
GO

-- Checks first, outside the transaction: none of them changes anything.
-- Read in the wrong encoding, every name below would be written garbled.
IF CAST(N'Phường Hải Châu' AS VARBINARY(64)) <> CAST(CONCAT(N'Ph', NCHAR(432), NCHAR(7901), N'ng H', NCHAR(7843), N'i Ch', NCHAR(226), N'u') AS VARBINARY(64))
    THROW 50000, N'This file was not read as UTF-8: run sqlcmd with -f 65001. Nothing changed.', 1;
IF NOT EXISTS (SELECT 1 FROM AdministrativeUnits WHERE unit_id = 1 AND unit_type = 'PROVINCE')
    THROW 50001, N'Unit 1 is not the province: this database was not built from db/StreetBiz_SQL_Server.sql. Nothing changed.', 1;
IF OBJECT_ID(N'dbo.UserAccounts') IS NULL OR OBJECT_ID(N'dbo.BusinessRegistrations') IS NULL OR OBJECT_ID(N'dbo.PricingZones') IS NULL
   OR OBJECT_ID(N'dbo.PenaltyFeeSchedules') IS NULL OR OBJECT_ID(N'dbo.WardCompliancePolicies') IS NULL
    THROW 50001, N'This database is older than the current schema: rebuild it with scripts/setup-local-db.ps1 -Recreate. Nothing changed.', 1;

BEGIN TRAN;

DECLARE @units TABLE (unit_id INT PRIMARY KEY, unit_name NVARCHAR(200) NOT NULL);
INSERT INTO @units VALUES
    (10, N'Phường Hải Châu'),
    (101, N'Phường Hòa Cường'),
    (11, N'Phường Thanh Khê'),
    (102, N'Phường An Khê'),
    (12, N'Phường An Hải'),
    (103, N'Phường Sơn Trà'),
    (14, N'Phường Ngũ Hành Sơn'),
    (104, N'Phường Hòa Khánh'),
    (105, N'Phường Hải Vân'),
    (106, N'Phường Liên Chiểu'),
    (107, N'Phường Cẩm Lệ'),
    (108, N'Phường Hòa Xuân'),
    (109, N'Phường Tam Kỳ'),
    (110, N'Phường Quảng Phú'),
    (111, N'Phường Hương Trà'),
    (112, N'Phường Bàn Thạch'),
    (113, N'Phường Điện Bàn'),
    (114, N'Phường Điện Bàn Đông'),
    (115, N'Phường An Thắng'),
    (116, N'Phường Điện Bàn Bắc'),
    (117, N'Phường Hội An'),
    (118, N'Phường Hội An Đông'),
    (119, N'Phường Hội An Tây'),
    (120, N'Xã Hòa Vang'),
    (121, N'Xã Hòa Tiến'),
    (122, N'Xã Bà Nà'),
    (123, N'Xã Núi Thành'),
    (124, N'Xã Tam Mỹ'),
    (125, N'Xã Tam Anh'),
    (126, N'Xã Đức Phú'),
    (127, N'Xã Tam Xuân'),
    (128, N'Xã Tây Hồ'),
    (129, N'Xã Chiên Đàn'),
    (130, N'Xã Phú Ninh'),
    (131, N'Xã Lãnh Ngọc'),
    (132, N'Xã Tiên Phước'),
    (133, N'Xã Thạnh Bình'),
    (134, N'Xã Sơn Cẩm Hà'),
    (135, N'Xã Trà Liên'),
    (136, N'Xã Trà Giáp'),
    (137, N'Xã Trà Tân'),
    (138, N'Xã Trà Đốc'),
    (139, N'Xã Trà My'),
    (140, N'Xã Nam Trà My'),
    (141, N'Xã Trà Tập'),
    (142, N'Xã Trà Vân'),
    (143, N'Xã Trà Linh'),
    (144, N'Xã Trà Leng'),
    (145, N'Xã Thăng Bình'),
    (146, N'Xã Thăng An'),
    (147, N'Xã Thăng Trường'),
    (148, N'Xã Thăng Điền'),
    (149, N'Xã Thăng Phú'),
    (150, N'Xã Đồng Dương'),
    (151, N'Xã Quế Sơn Trung'),
    (152, N'Xã Quế Sơn'),
    (153, N'Xã Xuân Phú'),
    (154, N'Xã Nông Sơn'),
    (155, N'Xã Quế Phước'),
    (156, N'Xã Duy Nghĩa'),
    (157, N'Xã Nam Phước'),
    (158, N'Xã Duy Xuyên'),
    (159, N'Xã Thu Bồn'),
    (160, N'Xã Điện Bàn Tây'),
    (161, N'Xã Gò Nổi'),
    (162, N'Xã Đại Lộc'),
    (163, N'Xã Hà Nha'),
    (164, N'Xã Thượng Đức'),
    (165, N'Xã Vu Gia'),
    (166, N'Xã Phú Thuận'),
    (167, N'Xã Thạnh Mỹ'),
    (168, N'Xã Bến Giằng'),
    (169, N'Xã Nam Giang'),
    (170, N'Xã Đắc Pring'),
    (171, N'Xã La Dêê'),
    (172, N'Xã La Êê'),
    (173, N'Xã Sông Vàng'),
    (174, N'Xã Sông Kôn'),
    (175, N'Xã Đông Giang'),
    (176, N'Xã Bến Hiên'),
    (177, N'Xã Avương'),
    (178, N'Xã Tây Giang'),
    (179, N'Xã Hùng Sơn'),
    (180, N'Xã Hiệp Đức'),
    (181, N'Xã Việt An'),
    (182, N'Xã Phước Trà'),
    (183, N'Xã Khâm Đức'),
    (184, N'Xã Phước Năng'),
    (185, N'Xã Phước Chánh'),
    (186, N'Xã Phước Thành'),
    (187, N'Xã Phước Hiệp'),
    (188, N'Xã Tam Hải'),
    (189, N'Xã Tân Hiệp'),
    (190, N'Đặc khu Hoàng Sa');

-- Wards that are not among the 94 (in a database from the old seed: 13 Nam Dương).
DECLARE @gone TABLE (unit_id INT PRIMARY KEY);
INSERT INTO @gone (unit_id)
SELECT unit_id FROM AdministrativeUnits
WHERE unit_type = 'WARD' AND unit_id NOT IN (SELECT unit_id FROM @units);

DECLARE @accounts INT = (SELECT COUNT(*) FROM UserAccounts WHERE ward_unit_id IN (SELECT unit_id FROM @gone)),
        @registrations INT = (SELECT COUNT(*) FROM BusinessRegistrations WHERE ward_unit_id IN (SELECT unit_id FROM @gone)),
        @zones INT = (SELECT COUNT(*) FROM PricingZones WHERE ward_unit_id IN (SELECT unit_id FROM @gone)),
        @rates INT = (SELECT COUNT(*) FROM PenaltyFeeSchedules WHERE ward_unit_id IN (SELECT unit_id FROM @gone)),
        @policies INT = (SELECT COUNT(*) FROM WardCompliancePolicies WHERE ward_unit_id IN (SELECT unit_id FROM @gone));
IF @accounts + @registrations + @zones + @rates + @policies > 0
BEGIN
    DECLARE @stop NVARCHAR(2048) = CONCAT(
        N'A ward that is not among the 94 (e.g. 13, now part of phuong Hai Chau = 10) is still used by ',
        @accounts, N' UserAccounts, ', @registrations, N' BusinessRegistrations, ', @zones, N' PricingZones, ',
        @rates, N' PenaltyFeeSchedules and ', @policies, N' WardCompliancePolicies rows. ',
        N'Move those rows to one of the 94 units, then run again. Nothing changed.');
    THROW 50002, @stop, 1;
END;

UPDATE a SET a.unit_name = u.unit_name, a.parent_unit_id = 1
FROM AdministrativeUnits a JOIN @units u ON u.unit_id = a.unit_id
WHERE a.unit_type = 'WARD';

SET IDENTITY_INSERT AdministrativeUnits ON;
INSERT INTO AdministrativeUnits (unit_id, unit_type, unit_name, parent_unit_id)
SELECT u.unit_id, 'WARD', u.unit_name, 1 FROM @units u
WHERE NOT EXISTS (SELECT 1 FROM AdministrativeUnits a WHERE a.unit_id = u.unit_id);
SET IDENTITY_INSERT AdministrativeUnits OFF;

DELETE FROM AdministrativeUnits WHERE unit_id IN (SELECT unit_id FROM @gone);
UPDATE AdministrativeUnits SET parent_unit_id = 1
WHERE parent_unit_id IN (SELECT unit_id FROM AdministrativeUnits WHERE unit_type = 'DISTRICT');
DELETE FROM AdministrativeUnits WHERE unit_type = 'DISTRICT';

-- An id among the 94 already taken by a non-ward row would leave a unit missing.
IF (SELECT COUNT(*) FROM AdministrativeUnits a
    JOIN @units u ON u.unit_id = a.unit_id AND u.unit_name = a.unit_name
    WHERE a.unit_type = 'WARD' AND a.parent_unit_id = 1) <> 94
   OR (SELECT COUNT(*) FROM AdministrativeUnits WHERE unit_type <> 'PROVINCE') <> 94
    THROW 50003, N'AdministrativeUnits would not end up as the 94 units of the seed. Nothing changed.', 1;

COMMIT;

SELECT unit_type, COUNT(*) AS units FROM AdministrativeUnits GROUP BY unit_type;
