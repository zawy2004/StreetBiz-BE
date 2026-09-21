/* ============================================================
   StreetBiz — demo dataset for LOCAL development

   Creates the people and the scenario: one account per role, ward configuration,
   the Nguyễn Văn Linh pilot corridor, and business registrations covering every
   status the review workflow can produce.

   Every account's password is  Password123!
   (BR-59: >= 8 chars, upper, lower, digit, symbol, no whitespace.)
   See docs/database.md for the full account list.

   The password_hash values are real BCrypt work-factor-12 hashes produced by the
   project's own hasher (src/StreetBiz.Infrastructure/Identity/PasswordHasher.cs).
   To regenerate, hash "Password123!" with BCrypt.Net-Next at work factor 12 --
   never invent a hash by hand, and never paste a plaintext password into a column.

   Idempotent: safe to run more than once. Ids are pinned with IDENTITY_INSERT so
   the scenario is stable and docs can refer to rows by number.

   Prerequisite: db/StreetBiz_SQL_Server.sql (schema + reference data: roles, wards,
   violation types). scripts/setup-local-db.ps1 runs both files in order.

   This is the ONLY seed file. Demo data that other seed scripts used to carry (the
   Nguyen Van Linh corridor, contracts, storefronts, ward compliance rates) lives here.
   When a schema change needs demo rows, add them to the matching section below.

   Do not run against a shared or production database.
   ============================================================ */

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF NOT EXISTS (SELECT 1 FROM Roles) OR NOT EXISTS (SELECT 1 FROM AdministrativeUnits WHERE unit_type = 'WARD')
BEGIN
    RAISERROR(N'Reference data missing. Run db/StreetBiz_SQL_Server.sql first.', 16, 1);
    SET NOEXEC ON;
END;
GO


/* ============================================================
   1. ACCOUNTS
   One per role, plus four vendors to carry the registration scenarios.
   ward_unit_id is NULL only for the platform administrator: a WARD_AUTHORITY
   without a ward is rejected by WardActorResolver, so every officer gets one.
   ============================================================ */

IF NOT EXISTS (SELECT 1 FROM UserAccounts)
BEGIN
    SET IDENTITY_INSERT UserAccounts ON;

    INSERT INTO UserAccounts
        (user_id, phone_number, password_hash, full_name, role_code, ward_unit_id, account_status, phone_verified_at)
    VALUES
        -- Platform administrator (ADM-xx). No ward: they are not a ward actor.
        ( 1, '0900000001', '$2a$12$JKEHknbT9p45.1ftBRU.tu8AydaqFuuPI4NTNUPRdnBbOlfviRTx2',
             N'Trần Quản Trị',      'PLATFORM_ADMIN', NULL, 'ACTIVE', SYSUTCDATETIME()),

        -- Ward officers (WARD-xx, REG-06). scripts/e2e-auth-onboarding.sh signs in as the first.
        ( 2, '0983000001', '$2a$12$Zi2yzfahgQFN8mvwwJyNr.wud3D8giLWQA7E7qXWeuILXQ0K2qvTm',
             N'Nguyễn Thị Hồng Vân', 'WARD_AUTHORITY', 10, 'ACTIVE', SYSUTCDATETIME()),
        ( 3, '0983000002', '$2a$12$vY2IB4IXX7MGXwtZ1H0AkuJpjTTo6AUZJuLtPdBzVmQHu89gqdjMO',
             N'Trần Quốc Bảo',       'WARD_AUTHORITY', 11, 'ACTIVE', SYSUTCDATETIME()),
        ( 4, '0983000003', '$2a$12$kZXarpH37Bj0GR.rPlbvneEH0wrgAlJ/wZkClm.eF3eMmH2GwL0Fq',
             N'Lê Thị Minh Thu',     'WARD_AUTHORITY', 12, 'ACTIVE', SYSUTCDATETIME()),

        -- Vendors (REG-xx, SIDE-xx). Each carries a different registration scenario.
        ( 5, '0905000101', '$2a$12$mUw/sNqNV5nanEk0.nVzeuXkFfbMefF568bBRklyTeaUoBCrBeVt.',
             N'Phạm Thị Lan',        'VENDOR', 10, 'ACTIVE', SYSUTCDATETIME()),
        ( 6, '0905000102', '$2a$12$TtGD6V0G8dFyTmbi.Mzs9.ZvGiDl2nM7KtMWI8Q8JneRojLJYC4Am',
             N'Hoàng Văn Tám',       'VENDOR', 10, 'ACTIVE', SYSUTCDATETIME()),
        ( 7, '0905000103', '$2a$12$KVMF6ASbLWS0UiFp2dp7i.Gq8NApv94LV8J5evGVX/rpIeOdMDc7m',
             N'Đỗ Thị Mai',          'VENDOR', 11, 'ACTIVE', SYSUTCDATETIME()),
        ( 8, '0905000104', '$2a$12$hRJFILqTiDNqwes4DvQ9HemEqUkA7UI7/M6tK6MBhYJl4jw08zefy',
             N'Vũ Minh Khôi',        'VENDOR', 10, 'ACTIVE', SYSUTCDATETIME()),

        -- Customers (BUY-xx, DISC-xx).
        ( 9, '0905000201', '$2a$12$.ONxPZji9PcRb.t0P5Mzv.vSJZQrEX2yQ8LA29QfqDzRDAFJfFLKO',
             N'Nguyễn Khách Hàng',   'CUSTOMER', 10, 'ACTIVE', SYSUTCDATETIME()),
        (10, '0905000202', '$2a$12$RcLiX4dVUY9WIa80ipW22.jxVeB5pq670Iu6dajDGCZSs76Vi26Rq',
             N'Lý Thu Hà',           'CUSTOMER', 12, 'ACTIVE', SYSUTCDATETIME());

    SET IDENTITY_INSERT UserAccounts OFF;
END;
GO

-- Vendors: one row per VENDOR account. The API creates this row itself during
-- registration (UserAccountRepository), so only fill the gap for seeded accounts.
IF NOT EXISTS (SELECT 1 FROM Vendors)
BEGIN
    SET IDENTITY_INSERT Vendors ON;
    INSERT INTO Vendors (vendor_id, user_id) VALUES (1, 5), (2, 6), (3, 7), (4, 8);
    SET IDENTITY_INSERT Vendors OFF;
END;
GO


/* ============================================================
   2. PLATFORM & WARD CONFIGURATION
   These carry a created_by pinned to a role by composite FK, so they cannot
   exist before the accounts above.
   ============================================================ */

-- FoodCategories.created_by must be a PLATFORM_ADMIN (FK_FoodCategories_CreatedBy).
-- The menu items in section 7 join to these by name; without them they would
-- silently insert zero rows.
IF NOT EXISTS (SELECT 1 FROM FoodCategories)
BEGIN
    SET IDENTITY_INSERT FoodCategories ON;
    INSERT INTO FoodCategories (category_id, category_name, created_by) VALUES
        ( 1, N'Món nước',        1),
        ( 2, N'Cơm - Bún - Phở', 1),
        ( 3, N'Bánh mì - Xôi',   1),
        ( 4, N'Đồ uống',         1),
        ( 5, N'Ăn vặt',          1),
        ( 6, N'Chè - Tráng miệng', 1),
        ( 7, N'Hải sản',         1),
        ( 8, N'Đồ chay',         1),
        ( 9, N'Trái cây',        1),
        (10, N'Khác',            1);
    SET IDENTITY_INSERT FoodCategories OFF;
END;
GO

-- WARD-03 penalty rates. One open rate per (ward, violation type) --
-- UQ_PenaltyFeeSchedules_CurrentRate enforces it via a filtered unique index.
IF NOT EXISTS (SELECT 1 FROM PenaltyFeeSchedules)
BEGIN
    INSERT INTO PenaltyFeeSchedules (ward_unit_id, violation_type, penalty_amount, created_by)
    SELECT w.ward_unit_id, v.violation_type_code, v.amount, w.officer_id
    FROM (VALUES (10, CAST(2 AS BIGINT)), (11, CAST(3 AS BIGINT)), (12, CAST(4 AS BIGINT))) AS w(ward_unit_id, officer_id)
    CROSS JOIN (VALUES
        ('NO_PERMIT',             CAST(2000000 AS DECIMAL(18,0))),
        ('OUTSIDE_SLOT',           CAST( 500000 AS DECIMAL(18,0))),
        ('OUTSIDE_HOURS',          CAST( 500000 AS DECIMAL(18,0))),
        ('BLOCK_PEDESTRIAN',       CAST(1000000 AS DECIMAL(18,0))),
        ('BLOCK_EMERGENCY',        CAST(3000000 AS DECIMAL(18,0))),
        ('HYGIENE_VIOLATION',      CAST(1500000 AS DECIMAL(18,0))),
        ('WASTE_DISPOSAL',         CAST( 750000 AS DECIMAL(18,0))),
        ('NOISE_VIOLATION',        CAST( 400000 AS DECIMAL(18,0))),
        ('UNAUTHORIZED_TRANSFER',  CAST(2500000 AS DECIMAL(18,0))),
        ('PERMIT_TAMPERING',       CAST(5000000 AS DECIMAL(18,0)))
    ) AS v(violation_type_code, amount);
END;
GO

-- WARD-03 / WARD-12: the five legal-citation violation types, priced per ward with the
-- Nghi dinh they come from (PenaltyFeeSchedules.legal_basis). AI only classifies a
-- violation onto one of these codes and drafts wording from legal_basis; it never
-- invents a citation. Amounts sit inside the statutory range quoted in each row.
IF NOT EXISTS (SELECT 1 FROM PenaltyFeeSchedules WHERE violation_type = 'UNAUTHORIZED_BUSINESS_USE')
BEGIN
    INSERT INTO PenaltyFeeSchedules (ward_unit_id, violation_type, penalty_amount, legal_basis, created_by)
    SELECT w.ward_unit_id, v.violation_type_code, v.amount, v.legal_basis, w.officer_id
    FROM (VALUES (10, CAST(2 AS BIGINT)), (11, CAST(3 AS BIGINT)), (12, CAST(4 AS BIGINT))) AS w(ward_unit_id, officer_id)
    CROSS JOIN (VALUES
        ('UNAUTHORIZED_BUSINESS_USE', CAST(2500000 AS DECIMAL(18,0)),
            N'Nghị định 168/2024/NĐ-CP: sử dụng trái phép lòng đường, vỉa hè để kinh doanh dịch vụ ăn uống, bày bán hàng hóa (khung 2.000.000 - 3.000.000đ)'),
        ('EXPIRED_OR_INVALID_PERMIT', CAST(12500000 AS DECIMAL(18,0)),
            N'Nghị định 168/2024/NĐ-CP: sử dụng tạm thời lòng đường, vỉa hè không phép hoặc giấy phép hết giá trị/sai nội dung (khung 10.000.000 - 15.000.000đ)'),
        ('STREET_VENDING_RESTRICTED', CAST(225000 AS DECIMAL(18,0)),
            N'Nghị định 168/2024/NĐ-CP: bán hàng rong tại tuyến phố cấm (khung 200.000 - 250.000đ)'),
        ('HYGIENE_LITTERING',         CAST(1500000 AS DECIMAL(18,0)),
            N'Nghị định 45/2022/NĐ-CP: vứt, thải, để rác thải sinh hoạt trên vỉa hè, lòng đường (khung 1.000.000 - 2.000.000đ)'),
        ('OBSTRUCT_PUBLIC_ORDER',     CAST(4000000 AS DECIMAL(18,0)),
            N'Nghị định 144/2021/NĐ-CP: đổ rác, vật cản gây mất an ninh trật tự công cộng (khung 3.000.000 - 5.000.000đ)')
    ) AS v(violation_type_code, amount, legal_basis);
END;
GO

-- WARD-13: a sanction decision is signed by the ward chairman, so the officer's title is a
-- fixed, admin-configured column (UserAccounts.sanction_authority_title), never typed on
-- the form. The demo officers are given the title of their own ward.
UPDATE a
SET a.sanction_authority_title = CONCAT(N'Chủ tịch UBND ', u.unit_name)
FROM UserAccounts a
JOIN AdministrativeUnits u ON u.unit_id = a.ward_unit_id
WHERE a.role_code = 'WARD_AUTHORITY' AND a.sanction_authority_title IS NULL;
GO


/* ============================================================
   3. PRICING ZONES, SLOTS AND STREET CONTEXT
   The Nguyễn Văn Linh pilot corridor:
   NVL-01..10 walk the real road centreline at a 15 m pitch, NVL-11..20 mirror
   them 38 m across to the far carriageway. Keeping the geometry means the
   street-strip diagram and its straightness check still work.
   ============================================================ */

IF NOT EXISTS (SELECT 1 FROM PricingZones)
BEGIN
    SET IDENTITY_INSERT PricingZones ON;
    INSERT INTO PricingZones
        (zone_id, ward_unit_id, zone_name, price_per_day, available_from, available_to,
         created_by, zone_code, regulation_ref, segment_from, segment_to, application_deadline)
    VALUES
        (1, 10, N'Đường Nguyễn Văn Linh', 30000, '05:00', '22:00', 2,
            N'HC1-NVL', N'QĐ 1247/QĐ-UBND', N'Nút giao Hoàng Diệu', N'Cầu Rồng',
            DATEADD(DAY, 60, CAST(SYSUTCDATETIME() AS DATE))),
        (2, 10, N'Đường Hoàng Diệu',      25000, '06:00', '21:00', 2,
            N'HC1-HD',  N'QĐ 1247/QĐ-UBND', N'Nút giao Nguyễn Văn Linh', N'Chợ Hàn',
            DATEADD(DAY, 45, CAST(SYSUTCDATETIME() AS DATE))),
        (3, 11, N'Đường Điện Biên Phủ',   20000, '05:30', '22:00', 3,
            N'TKD-DBP', N'QĐ 0982/QĐ-UBND', N'Ngã ba Huế', N'Cầu vượt Hòa Cầm',
            DATEADD(DAY, 30, CAST(SYSUTCDATETIME() AS DATE))),
        (4, 12, N'Đường Ngô Quyền',       22000, '06:00', '22:00', 4,
            N'AHB-NQ',  N'QĐ 0771/QĐ-UBND', N'Cầu sông Hàn', N'Nút giao Phạm Văn Đồng',
            DATEADD(DAY, 30, CAST(SYSUTCDATETIME() AS DATE)));
    SET IDENTITY_INSERT PricingZones OFF;
END;
GO

-- Fee components (SIDE-02 quote). PER_DAY multiplies by the term; PER_TERM is charged once.
IF NOT EXISTS (SELECT 1 FROM ZoneFeeComponents)
BEGIN
    INSERT INTO ZoneFeeComponents (zone_id, component_name, calc_basis, unit_amount, sort_order)
    SELECT z.zone_id, c.component_name, c.calc_basis, c.unit_amount, c.sort_order
    FROM PricingZones z
    CROSS JOIN (VALUES
        (N'Phí vệ sinh môi trường', 'PER_DAY',  CAST(  3000 AS DECIMAL(18,0)), 1),
        (N'Phí quản lý hồ sơ',      'PER_TERM', CAST(150000 AS DECIMAL(18,0)), 2),
        (N'Tiền đặt cọc',           'PER_TERM', CAST(500000 AS DECIMAL(18,0)), 3)
    ) AS c(component_name, calc_basis, unit_amount, sort_order);
END;
GO

-- Technical corridors along zone 1. blocks_business = 1 marks a stretch no slot may occupy.
IF NOT EXISTS (SELECT 1 FROM StreetFeatures)
BEGIN
    INSERT INTO StreetFeatures (zone_id, feature_type, label, latitude, longitude, blocks_business, note) VALUES
        (1, 'TRANSFORMER', N'Trạm biến áp NVL-T1', 16.047120, 108.214900, 1, N'Hành lang an toàn lưới điện 3 m'),
        (1, 'HYDRANT',     N'Trụ nước chữa cháy',  16.047260, 108.216300, 1, N'Không được che chắn'),
        (1, 'TREE',        N'Cây xanh đô thị',     16.047190, 108.215600, 0, NULL),
        (1, 'LIGHT_POLE',  N'Cột đèn chiếu sáng',  16.047220, 108.215950, 0, NULL),
        (1, 'BUS_STOP',    N'Điểm dừng xe buýt',   16.047300, 108.216700, 1, N'Phạm vi 10 m mỗi bên'),
        (1, 'PARKING',     N'Vạch đỗ xe máy',      16.047150, 108.215200, 0, NULL);
END;
GO

-- 20 slots on the Nguyễn Văn Linh corridor, plus a handful in the other zones so
-- every ward has something on its map. All WARD_DEFINED: a VENDOR_PROPOSED slot
-- must carry proposal evidence (CK_SidewalkSlots_ProposalCoherent).
IF NOT EXISTS (SELECT 1 FROM SidewalkSlots)
BEGIN
    SET IDENTITY_INSERT SidewalkSlots ON;

    -- Near carriageway, NVL-01..10: 15 m pitch along the fitted road axis.
    INSERT INTO SidewalkSlots
        (slot_id, slot_code, zone_id, latitude, longitude, width_meters, length_meters,
         slot_status, source, has_power, has_water, has_trash_bin, business_category)
    VALUES
        ( 1, 'NVL-01', 1, 16.047050, 108.214700, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 1, 1, 1, 'FOOD_BEVERAGE'),
        ( 2, 'NVL-02', 1, 16.047063, 108.214840, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 1, 0, 1, 'FOOD_BEVERAGE'),
        ( 3, 'NVL-03', 1, 16.047076, 108.214980, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 0, 0, 1, 'RETAIL'),
        ( 4, 'NVL-04', 1, 16.047089, 108.215120, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 1, 1, 0, 'FOOD_BEVERAGE'),
        ( 5, 'NVL-05', 1, 16.047102, 108.215260, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 0, 0, 0, 'GENERAL'),
        ( 6, 'NVL-06', 1, 16.047115, 108.215400, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 1, 0, 1, 'FOOD_BEVERAGE'),
        ( 7, 'NVL-07', 1, 16.047128, 108.215540, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 0, 1, 1, 'CRAFTS'),
        ( 8, 'NVL-08', 1, 16.047141, 108.215680, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 1, 1, 1, 'FOOD_BEVERAGE'),
        ( 9, 'NVL-09', 1, 16.047154, 108.215820, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 0, 0, 0, 'SERVICES'),
        (10, 'NVL-10', 1, 16.047167, 108.215960, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 1, 0, 1, 'RETAIL'),

    -- Far carriageway, NVL-11..20: the same run mirrored 38 m across the road.
        (11, 'NVL-11', 1, 16.047392, 108.214700, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 1, 1, 1, 'FOOD_BEVERAGE'),
        (12, 'NVL-12', 1, 16.047405, 108.214840, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 0, 0, 1, 'FOOD_BEVERAGE'),
        (13, 'NVL-13', 1, 16.047418, 108.214980, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 1, 0, 0, 'RETAIL'),
        (14, 'NVL-14', 1, 16.047431, 108.215120, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 0, 1, 1, 'GENERAL'),
        (15, 'NVL-15', 1, 16.047444, 108.215260, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 1, 1, 1, 'FOOD_BEVERAGE'),
        (16, 'NVL-16', 1, 16.047457, 108.215400, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 0, 0, 0, 'SERVICES'),
        (17, 'NVL-17', 1, 16.047470, 108.215540, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 1, 0, 1, 'CRAFTS'),
        (18, 'NVL-18', 1, 16.047483, 108.215680, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 0, 1, 0, 'FOOD_BEVERAGE'),
        (19, 'NVL-19', 1, 16.047496, 108.215820, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 1, 1, 1, 'FOOD_BEVERAGE'),
        (20, 'NVL-20', 1, 16.047509, 108.215960, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 0, 0, 1, 'RETAIL'),

    -- A few slots in the other zones so every ward map has content.
        (21, 'HD-01',  2, 16.069500, 108.222100, 2.00, 2.50, 'AVAILABLE', 'WARD_DEFINED', 0, 0, 1, 'FOOD_BEVERAGE'),
        (22, 'HD-02',  2, 16.069620, 108.222240, 2.00, 2.50, 'AVAILABLE', 'WARD_DEFINED', 1, 0, 1, 'RETAIL'),
        (23, 'HD-03',  2, 16.069740, 108.222380, 2.00, 2.50, 'AVAILABLE', 'WARD_DEFINED', 0, 1, 0, 'GENERAL'),
        (24, 'DBP-01', 3, 16.061200, 108.201400, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 1, 1, 1, 'FOOD_BEVERAGE'),
        (25, 'DBP-02', 3, 16.061340, 108.201560, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 0, 0, 1, 'CRAFTS'),
        (26, 'NQ-01',  4, 16.075800, 108.227300, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 1, 0, 1, 'FOOD_BEVERAGE'),
        (27, 'NQ-02',  4, 16.075940, 108.227460, 2.00, 3.00, 'AVAILABLE', 'WARD_DEFINED', 0, 1, 1, 'RETAIL');

    SET IDENTITY_INSERT SidewalkSlots OFF;
END;
GO

/* ============================================================
   4. BUSINESS REGISTRATIONS (REG-01..06)
   One registration per status in CK_BusinessRegistrations_Status, so the ward
   queue, the vendor tracking list and every status chip have a live example.

   BR-09 (at most one SUBMITTED/UNDER_REVIEW registration per vendor) is an
   application-layer rule; the seed honours it -- vendor 4 carries the five
   closed/draft states and only one open one.

   A FIXED_STOREFRONT must declare an address (CK_BusinessRegistrations_FixedNeedsAddress);
   an ITINERANT need not. A reviewed row's reviewed_by must hold WARD_AUTHORITY
   (FK_BusinessRegistrations_Reviewer).
   ============================================================ */

IF NOT EXISTS (SELECT 1 FROM BusinessRegistrations)
BEGIN
    SET IDENTITY_INSERT BusinessRegistrations ON;

    INSERT INTO BusinessRegistrations
        (registration_id, vendor_id, vendor_type, display_name, declared_address,
         address_latitude, address_longitude, ward_unit_id, registration_status,
         fast_track_flag, reviewed_by, review_decision_reason, reviewed_at, created_at)
    VALUES
        -- APPROVED, with a contract, permit, fees and a storefront underneath it.
        (1, 1, 'FIXED_STOREFRONT', N'Bánh mì & Xôi Cô Lan',
            N'123 Nguyễn Văn Linh, Phường Hải Châu 1, Đà Nẵng', 16.047050, 108.214700, 10,
            'APPROVED', 0, 2, N'Hồ sơ đầy đủ, vị trí phù hợp quy hoạch vỉa hè.',
            DATEADD(DAY, -28, SYSUTCDATETIME()), DATEADD(DAY, -32, SYSUTCDATETIME())),

        -- SUBMITTED with complete evidence: the ward sees REVIEW/APPROVE/REJECT/REQUEST_INFO.
        (2, 2, 'ITINERANT', N'Xe nước mía Tám Ù', NULL, NULL, NULL, 10,
            'SUBMITTED', 0, NULL, NULL, NULL, DATEADD(DAY, -3, SYSUTCDATETIME())),

        -- SUBMITTED with NO evidence: BR-07 blocks approval, so the ward sees the
        -- blocker banner and only REVIEW/REJECT/REQUEST_INFO.
        (3, 3, 'FIXED_STOREFRONT', N'Quán chè Đỗ Mai',
            N'88 Điện Biên Phủ, Phường Thanh Khê Đông, Đà Nẵng', 16.061200, 108.201400, 11,
            'SUBMITTED', 1, NULL, NULL, NULL, DATEADD(DAY, -1, SYSUTCDATETIME())),

        -- UNDER_REVIEW: an officer has claimed the case.
        (4, 4, 'ITINERANT', N'Gánh bún Minh Khôi', NULL, NULL, NULL, 10,
            'UNDER_REVIEW', 0, 2, NULL, DATEADD(DAY, -1, SYSUTCDATETIME()),
            DATEADD(DAY, -5, SYSUTCDATETIME())),

        -- MORE_INFORMATION_REQUIRED: the vendor sees the reason and can edit (REG-04).
        (5, 4, 'ITINERANT', N'Xe bánh tráng nướng', NULL, NULL, NULL, 10,
            'MORE_INFORMATION_REQUIRED', 0, 2,
            N'Ảnh CCCD mặt sau bị mờ, đề nghị chụp lại rõ nét.',
            DATEADD(DAY, -6, SYSUTCDATETIME()), DATEADD(DAY, -9, SYSUTCDATETIME())),

        -- REJECTED.
        (6, 4, 'FIXED_STOREFRONT', N'Quầy cà phê vỉa hè',
            N'12 Bạch Đằng, Phường Hải Châu 1, Đà Nẵng', 16.070200, 108.224100, 10,
            'REJECTED', 0, 2,
            N'Vị trí đăng ký nằm trong hành lang an toàn giao thông, không thuộc khu vực được phép kinh doanh.',
            DATEADD(DAY, -14, SYSUTCDATETIME()), DATEADD(DAY, -18, SYSUTCDATETIME())),

        -- WITHDRAWN by the vendor (REG-05).
        (7, 4, 'ITINERANT', N'Xe hoa quả dạo', NULL, NULL, NULL, 10,
            'WITHDRAWN', 0, NULL, NULL, NULL, DATEADD(DAY, -21, SYSUTCDATETIME())),

        -- DRAFT: never submitted, visible only to its owner.
        (8, 4, 'ITINERANT', N'Quầy đồ chay (nháp)', NULL, NULL, NULL, 10,
            'DRAFT', 0, NULL, NULL, NULL, DATEADD(DAY, -2, SYSUTCDATETIME())),

        -- A second APPROVED registration for vendor 1 (BR-06 allows several).
        (9, 1, 'FIXED_STOREFRONT', N'Bún chả Hải Châu',
            N'45 Nguyễn Văn Linh, Phường Hải Châu 1, Đà Nẵng', 16.047141, 108.215680, 10,
            'APPROVED', 0, 2, N'Đủ điều kiện kinh doanh cố định trên vỉa hè.',
            DATEADD(DAY, -11, SYSUTCDATETIME()), DATEADD(DAY, -15, SYSUTCDATETIME()));

    SET IDENTITY_INSERT BusinessRegistrations OFF;
END;
GO

/* Evidence (REG-02).
   EvidenceTypes.RequiredFor(): IDENTITY_DOCUMENT always, plus BUSINESS_LICENSE for
   a fixed storefront. Registration 3 is deliberately left with none so the ward
   blocker path is demonstrable.

   file_url must be the shape EvidenceFiles.BuildUrl() produces --
   /api/uploads/evidence/{ownerUserId}/{fileName} -- and the file name must satisfy
   EvidenceFiles.IsValidFileName(): exactly 32 lowercase hex characters plus a
   .jpg/.png/.webp/.pdf extension. A readable name like "cccd-lan.jpg" is rejected
   by TryParseUrl and the document download 404s before it reaches disk.

   The matching files must also exist under Storage:RootPath at
   evidence/{ownerUserId}/{fileName} (EvidenceFiles.StoragePath).
   scripts/setup-local-db.ps1 writes them.

   retention_expires_at is EvidenceFiles.RetentionPeriod (730 days, BR-47). */
IF NOT EXISTS (SELECT 1 FROM RegistrationEvidence)
BEGIN
    INSERT INTO RegistrationEvidence
        (registration_id, evidence_type, file_url, uploaded_at, retention_expires_at)
    VALUES
        (1, 'IDENTITY_DOCUMENT', '/api/uploads/evidence/5/0000000000000000000000000000ab01.jpg',
            DATEADD(DAY, -32, SYSUTCDATETIME()), DATEADD(DAY, 698, SYSUTCDATETIME())),
        (1, 'BUSINESS_LICENSE',  '/api/uploads/evidence/5/0000000000000000000000000000ab02.pdf',
            DATEADD(DAY, -32, SYSUTCDATETIME()), DATEADD(DAY, 698, SYSUTCDATETIME())),
        (2, 'IDENTITY_DOCUMENT', '/api/uploads/evidence/6/0000000000000000000000000000ab04.jpg',
            DATEADD(DAY, -3, SYSUTCDATETIME()),  DATEADD(DAY, 727, SYSUTCDATETIME())),
        (4, 'IDENTITY_DOCUMENT', '/api/uploads/evidence/8/0000000000000000000000000000ab05.jpg',
            DATEADD(DAY, -5, SYSUTCDATETIME()),  DATEADD(DAY, 725, SYSUTCDATETIME())),
        (5, 'IDENTITY_DOCUMENT', '/api/uploads/evidence/8/0000000000000000000000000000ab06.jpg',
            DATEADD(DAY, -9, SYSUTCDATETIME()),  DATEADD(DAY, 721, SYSUTCDATETIME())),
        (9, 'IDENTITY_DOCUMENT', '/api/uploads/evidence/5/0000000000000000000000000000ab01.jpg',
            DATEADD(DAY, -15, SYSUTCDATETIME()), DATEADD(DAY, 715, SYSUTCDATETIME())),
        (9, 'BUSINESS_LICENSE',  '/api/uploads/evidence/5/0000000000000000000000000000ab03.pdf',
            DATEADD(DAY, -15, SYSUTCDATETIME()), DATEADD(DAY, 715, SYSUTCDATETIME()));
END;
GO


/* ============================================================
   5. RENTAL APPLICATIONS, CONTRACTS AND PERMITS (SIDE-xx, WARD-08)
   BR-16: an application may only be approved against an APPROVED registration,
   so only registrations 1 and 9 carry contracts.
   ============================================================ */

IF NOT EXISTS (SELECT 1 FROM RentalApplications)
BEGIN
    SET IDENTITY_INSERT RentalApplications ON;
    INSERT INTO RentalApplications
        (application_id, registration_id, slot_id, application_method, requested_term_days,
         application_status, reviewed_by, review_decision_reason, reviewed_at,
         created_at, commitments_accepted_at)
    VALUES
        (1, 1, 1, 'MANUAL_SELECTED',  90, 'APPROVED', 2,
            N'Ô NVL-01 còn trống, hồ sơ hợp lệ.',
            DATEADD(DAY, -30, SYSUTCDATETIME()), DATEADD(DAY, -31, SYSUTCDATETIME()), DATEADD(DAY, -31, SYSUTCDATETIME())),
        (2, 9, 8, 'MANUAL_SELECTED', 180, 'APPROVED', 2,
            N'Chấp thuận thuê dài hạn ô NVL-08.',
            DATEADD(DAY, -10, SYSUTCDATETIME()), DATEADD(DAY, -11, SYSUTCDATETIME()), DATEADD(DAY, -11, SYSUTCDATETIME())),
        -- Open application, so the ward has a pending rental case to work on.
        (3, 1, 3, 'MANUAL_SELECTED',  60, 'PENDING', NULL, NULL, NULL,
            DATEADD(DAY, -2, SYSUTCDATETIME()), DATEADD(DAY, -2, SYSUTCDATETIME()));
    SET IDENTITY_INSERT RentalApplications OFF;
END;
GO

/* TR_RentalContracts_NoOverlap: one slot cannot be let twice over overlapping
   dates. The two contracts below sit on different slots (1 and 8). */
IF NOT EXISTS (SELECT 1 FROM RentalContracts)
BEGIN
    SET IDENTITY_INSERT RentalContracts ON;
    INSERT INTO RentalContracts
        (contract_id, application_id, slot_id, vendor_id, start_date, end_date, contract_status, created_at)
    VALUES
        (1, 1, 1, 1, DATEADD(DAY, -30, CAST(SYSUTCDATETIME() AS DATE)),
                     DATEADD(DAY,  60, CAST(SYSUTCDATETIME() AS DATE)), 'ACTIVE', DATEADD(DAY, -30, SYSUTCDATETIME())),
        (2, 2, 8, 1, DATEADD(DAY, -10, CAST(SYSUTCDATETIME() AS DATE)),
                     DATEADD(DAY, 170, CAST(SYSUTCDATETIME() AS DATE)), 'ACTIVE', DATEADD(DAY, -10, SYSUTCDATETIME()));
    SET IDENTITY_INSERT RentalContracts OFF;
END;
GO

-- Occupied slots follow their contract; slot 3 has an open application.
UPDATE SidewalkSlots SET slot_status = 'ACTIVE'              WHERE slot_id IN (1, 8) AND slot_status <> 'ACTIVE';
UPDATE SidewalkSlots SET slot_status = 'PENDING_APPLICATION' WHERE slot_id = 3       AND slot_status = 'AVAILABLE';
GO

/* SIDE-08 permits. qr_payload is a placeholder, NOT a real IPermitTokenService
   token -- scanning one of these will not verify. Re-issue through the API when
   testing WARD-11 / BUY-02 scan flows. */
IF NOT EXISTS (SELECT 1 FROM DigitalPermits)
BEGIN
    SET IDENTITY_INSERT DigitalPermits ON;
    INSERT INTO DigitalPermits (permit_id, contract_id, qr_payload, permit_status, issued_at) VALUES
        (1, 1, 'SEED-PERMIT-CONTRACT-1-DO-NOT-SCAN', 'ACTIVE', DATEADD(DAY, -30, SYSUTCDATETIME())),
        (2, 2, 'SEED-PERMIT-CONTRACT-2-DO-NOT-SCAN', 'ACTIVE', DATEADD(DAY, -10, SYSUTCDATETIME()));
    SET IDENTITY_INSERT DigitalPermits OFF;
END;
GO


/* ============================================================
   6. FEES AND INVOICES (FEE-01..03)
   Zone 1: 30 000/day + 3 000/day cleaning + 150 000 admin + 500 000 deposit.
     contract 1  ( 90 days): 90*33 000 + 650 000 = 3 620 000
     contract 2  (180 days): 180*33 000 + 650 000 = 6 590 000
   ============================================================ */

IF NOT EXISTS (SELECT 1 FROM FeeSchedules)
BEGIN
    SET IDENTITY_INSERT FeeSchedules ON;
    INSERT INTO FeeSchedules (fee_schedule_id, contract_id, revision, total_amount, generated_at) VALUES
        (1, 1, 1, 3620000, DATEADD(DAY, -30, SYSUTCDATETIME())),
        (2, 2, 1, 6590000, DATEADD(DAY, -10, SYSUTCDATETIME()));
    SET IDENTITY_INSERT FeeSchedules OFF;

    SET IDENTITY_INSERT FeeScheduleItems ON;
    INSERT INTO FeeScheduleItems (fee_item_id, fee_schedule_id, due_date, amount, item_status, paid_at) VALUES
        -- Contract 1: one paid, one overdue, one upcoming -- all three states on screen.
        (1, 1, DATEADD(DAY, -30, CAST(SYSUTCDATETIME() AS DATE)), 1540000, 'PAID',
               DATEADD(DAY, -29, SYSUTCDATETIME())),
        (2, 1, DATEADD(DAY,  -2, CAST(SYSUTCDATETIME() AS DATE)), 1040000, 'OVERDUE', NULL),
        (3, 1, DATEADD(DAY,  28, CAST(SYSUTCDATETIME() AS DATE)), 1040000, 'PENDING', NULL),
        -- Contract 2: first instalment paid, the rest ahead.
        (4, 2, DATEADD(DAY, -10, CAST(SYSUTCDATETIME() AS DATE)), 2630000, 'PAID',
               DATEADD(DAY,  -9, SYSUTCDATETIME())),
        (5, 2, DATEADD(DAY,  50, CAST(SYSUTCDATETIME() AS DATE)), 1980000, 'PENDING', NULL),
        (6, 2, DATEADD(DAY, 110, CAST(SYSUTCDATETIME() AS DATE)), 1980000, 'PENDING', NULL);
    SET IDENTITY_INSERT FeeScheduleItems OFF;
END;
GO

-- BR-32: an invoice is issued only against a confirmed payment, so only the PAID items.
IF NOT EXISTS (SELECT 1 FROM Invoices)
BEGIN
    INSERT INTO Invoices (invoice_number, fee_item_id, penalty_id, vendor_id, amount, issued_at) VALUES
        (N'HD-2026-000001', 1, NULL, 1, 1540000, DATEADD(DAY, -29, SYSUTCDATETIME())),
        (N'HD-2026-000002', 4, NULL, 1, 2630000, DATEADD(DAY,  -9, SYSUTCDATETIME()));
END;
GO


/* ============================================================
   7. PHASE 2 MARKETPLACE (STORE-xx, DISC-xx, ORD-xx)
   TR_Storefronts_Phase2Gate (BR-48) admits a storefront only when its
   registration is APPROVED, its contract ACTIVE, and both belong to one vendor.
   ============================================================ */

IF NOT EXISTS (SELECT 1 FROM Storefronts)
BEGIN
    SET IDENTITY_INSERT Storefronts ON;
    INSERT INTO Storefronts
        (storefront_id, registration_id, contract_id, storefront_name, description, availability_status, created_at)
    VALUES
        (1, 1, 1, N'Bánh mì & Xôi Cô Lan',
            N'Bánh mì thịt nướng, xôi gà, xôi xéo. Bán từ 5h sáng tại ô NVL-01.',
            'OPEN', DATEADD(DAY, -28, SYSUTCDATETIME())),
        (2, 9, 2, N'Bún chả Hải Châu',
            N'Bún chả than hoa, nem cua bể. Đặt trước để lấy nhanh tại ô NVL-08.',
            'OPEN', DATEADD(DAY, -10, SYSUTCDATETIME()));
    SET IDENTITY_INSERT Storefronts OFF;
END;
GO

IF NOT EXISTS (SELECT 1 FROM StorefrontBusinessHours)
BEGIN
    INSERT INTO StorefrontBusinessHours (storefront_id, day_of_week, opens_at, closes_at)
    SELECT s.storefront_id, d.day_of_week, s.opens_at, s.closes_at
    FROM (VALUES
        (CAST(1 AS BIGINT), CAST('05:00' AS TIME), CAST('11:00' AS TIME)),
        (CAST(2 AS BIGINT), CAST('10:00' AS TIME), CAST('21:00' AS TIME))
    ) AS s(storefront_id, opens_at, closes_at)
    CROSS JOIN (VALUES (1),(2),(3),(4),(5),(6),(7)) AS d(day_of_week);
END;
GO

IF NOT EXISTS (SELECT 1 FROM MenuItems)
BEGIN
    SET IDENTITY_INSERT MenuItems ON;
    INSERT INTO MenuItems (menu_item_id, storefront_id, category_id, item_name, description, unit_price, availability_status) VALUES
        ( 1, 1, 3, N'Bánh mì thịt nướng', N'Bánh mì giòn, thịt nướng than hoa, rau thơm.', 25000, 'AVAILABLE'),
        ( 2, 1, 3, N'Bánh mì chả cá',     N'Chả cá chiên nóng, sốt đặc biệt.',             25000, 'AVAILABLE'),
        ( 3, 1, 3, N'Xôi gà xé',          N'Xôi nếp dẻo, gà xé phay, hành phi.',           30000, 'AVAILABLE'),
        ( 4, 1, 3, N'Xôi xéo',            N'Xôi đậu xanh, mỡ hành, chà bông.',             25000, 'AVAILABLE'),
        ( 5, 1, 4, N'Sữa đậu nành',       N'Sữa đậu nành nóng, không đường.',              10000, 'AVAILABLE'),
        ( 6, 1, 4, N'Cà phê sữa đá',      N'Cà phê phin truyền thống.',                    18000, 'SOLD_OUT'),

        ( 7, 2, 2, N'Bún chả Hà Nội',     N'Chả miếng, chả viên nướng than, nước mắm chua ngọt.', 45000, 'AVAILABLE'),
        ( 8, 2, 2, N'Bún chả đặc biệt',   N'Thêm nem cua bể và chả viên.',                 65000, 'AVAILABLE'),
        ( 9, 2, 5, N'Nem cua bể',         N'Nem vuông nhân cua bể, chiên giòn (2 cái).',   30000, 'AVAILABLE'),
        (10, 2, 4, N'Trà đá',             N'Trà xanh đá, miễn phí khi ăn tại chỗ.',         3000, 'AVAILABLE'),
        (11, 2, 4, N'Nước sâm',           N'Nước sâm lạnh nấu thủ công.',                 12000, 'AVAILABLE'),
        (12, 2, 6, N'Chè đỗ đen',         N'Chè đỗ đen nước cốt dừa.',                     20000, 'AVAILABLE');
    SET IDENTITY_INSERT MenuItems OFF;
END;
GO

IF NOT EXISTS (SELECT 1 FROM Orders)
BEGIN
    SET IDENTITY_INSERT Orders ON;
    INSERT INTO Orders
        (order_id, order_code, customer_user_id, storefront_id, order_status,
         subtotal_amount, total_amount, rejection_reason, placed_at, completed_at, created_at)
    VALUES
        (1, N'DH-2026-0001',  9, 1, 'COMPLETED',        60000, 60000, NULL,
            DATEADD(DAY, -6, SYSUTCDATETIME()), DATEADD(DAY, -6, SYSUTCDATETIME()), DATEADD(DAY, -6, SYSUTCDATETIME())),
        (2, N'DH-2026-0002', 10, 2, 'READY_FOR_PICKUP', 75000, 75000, NULL,
            DATEADD(HOUR, -2, SYSUTCDATETIME()), NULL, DATEADD(HOUR, -2, SYSUTCDATETIME())),
        (3, N'DH-2026-0003',  9, 2, 'PREPARING',        45000, 45000, NULL,
            DATEADD(MINUTE, -25, SYSUTCDATETIME()), NULL, DATEADD(MINUTE, -25, SYSUTCDATETIME())),
        (4, N'DH-2026-0004', 10, 1, 'PENDING_PAYMENT',  50000, 50000, NULL,
            NULL, NULL, DATEADD(MINUTE, -5, SYSUTCDATETIME())),
        (5, N'DH-2026-0005',  9, 1, 'REJECTED',         25000, 25000,
            N'Đã hết bánh mì thịt nướng trong hôm nay.',
            DATEADD(DAY, -2, SYSUTCDATETIME()), NULL, DATEADD(DAY, -2, SYSUTCDATETIME()));
    SET IDENTITY_INSERT Orders OFF;

    INSERT INTO OrderItems (order_id, menu_item_id, item_name_snapshot, unit_price_snapshot, quantity, note) VALUES
        (1,  1, N'Bánh mì thịt nướng', 25000, 2, N'Không rau mùi'),
        (1,  5, N'Sữa đậu nành',       10000, 1, NULL),
        (2,  8, N'Bún chả đặc biệt',   65000, 1, NULL),
        (2, 10, N'Trà đá',              3000, 1, NULL),
        (2,  9, N'Nem cua bể',         30000, 1, N'Giòn kỹ giúp em'),
        (3,  7, N'Bún chả Hà Nội',     45000, 1, NULL),
        (4,  3, N'Xôi gà xé',          30000, 1, NULL),
        (4,  4, N'Xôi xéo',            25000, 1, NULL),
        (5,  1, N'Bánh mì thịt nướng', 25000, 1, NULL);

    INSERT INTO OrderStatusHistory (order_id, from_status, to_status, changed_by, note, changed_at) VALUES
        (1, NULL,               'PENDING_PAYMENT',  9,    NULL,                         DATEADD(DAY, -6, SYSUTCDATETIME())),
        (1, 'PENDING_PAYMENT',  'PLACED',           NULL, N'Thanh toán đã xác nhận.',   DATEADD(DAY, -6, SYSUTCDATETIME())),
        (1, 'PLACED',           'ACCEPTED',         5,    NULL,                         DATEADD(DAY, -6, SYSUTCDATETIME())),
        (1, 'ACCEPTED',         'PREPARING',        5,    NULL,                         DATEADD(DAY, -6, SYSUTCDATETIME())),
        (1, 'PREPARING',        'READY_FOR_PICKUP', 5,    NULL,                         DATEADD(DAY, -6, SYSUTCDATETIME())),
        (1, 'READY_FOR_PICKUP', 'COMPLETED',        5,    N'Khách đã nhận hàng.',       DATEADD(DAY, -6, SYSUTCDATETIME())),

        (2, NULL,               'PENDING_PAYMENT', 10,    NULL,                         DATEADD(HOUR, -2, SYSUTCDATETIME())),
        (2, 'PENDING_PAYMENT',  'PLACED',           NULL, N'Thanh toán đã xác nhận.',   DATEADD(HOUR, -2, SYSUTCDATETIME())),
        (2, 'PLACED',           'ACCEPTED',         5,    NULL,                         DATEADD(MINUTE, -95, SYSUTCDATETIME())),
        (2, 'ACCEPTED',         'PREPARING',        5,    NULL,                         DATEADD(MINUTE, -80, SYSUTCDATETIME())),
        (2, 'PREPARING',        'READY_FOR_PICKUP', 5,    N'Đơn đã sẵn sàng.',          DATEADD(MINUTE, -60, SYSUTCDATETIME())),

        (3, NULL,               'PENDING_PAYMENT',  9,    NULL,                         DATEADD(MINUTE, -25, SYSUTCDATETIME())),
        (3, 'PENDING_PAYMENT',  'PLACED',           NULL, N'Thanh toán đã xác nhận.',   DATEADD(MINUTE, -24, SYSUTCDATETIME())),
        (3, 'PLACED',           'ACCEPTED',         5,    NULL,                         DATEADD(MINUTE, -20, SYSUTCDATETIME())),
        (3, 'ACCEPTED',         'PREPARING',        5,    NULL,                         DATEADD(MINUTE, -15, SYSUTCDATETIME())),

        (4, NULL,               'PENDING_PAYMENT', 10,    NULL,                         DATEADD(MINUTE, -5, SYSUTCDATETIME())),

        (5, NULL,               'PENDING_PAYMENT',  9,    NULL,                         DATEADD(DAY, -2, SYSUTCDATETIME())),
        (5, 'PENDING_PAYMENT',  'PLACED',           NULL, N'Thanh toán đã xác nhận.',   DATEADD(DAY, -2, SYSUTCDATETIME())),
        (5, 'PLACED',           'REJECTED',         5,    N'Đã hết bánh mì thịt nướng.', DATEADD(DAY, -2, SYSUTCDATETIME()));
END;
GO


/* ============================================================
   8. SIDEWALK WORKFLOWS (SIDE-06, 09..13, WARD-09, 16..18)
   Something in every queue and list the vendor and ward screens show, so no
   screen opens empty. Registration 10 is a second, APPROVED registration for
   vendor 2 with no contract: the natural receiver of a slot transfer and a
   vendor who can hold a slot and apply for one (BR-06 allows several).
   ============================================================ */

IF NOT EXISTS (SELECT 1 FROM BusinessRegistrations WHERE registration_id = 10)
BEGIN
    SET IDENTITY_INSERT BusinessRegistrations ON;
    INSERT INTO BusinessRegistrations
        (registration_id, vendor_id, vendor_type, display_name, declared_address,
         address_latitude, address_longitude, ward_unit_id, registration_status,
         fast_track_flag, reviewed_by, review_decision_reason, reviewed_at, created_at)
    VALUES
        (10, 2, 'ITINERANT', N'Xe nước ép Tám Ù', NULL, NULL, NULL, 10,
            'APPROVED', 0, 2, N'Hồ sơ đầy đủ, đủ điều kiện kinh doanh lưu động.',
            DATEADD(DAY, -8, SYSUTCDATETIME()), DATEADD(DAY, -12, SYSUTCDATETIME()));
    SET IDENTITY_INSERT BusinessRegistrations OFF;
END;
GO

-- Evidence for registration 10, and the CCCD back / portrait for registration 2 (eKYC).
-- The files are the placeholders scripts/setup-local-db.ps1 writes; several rows share one.
IF NOT EXISTS (SELECT 1 FROM RegistrationEvidence WHERE registration_id = 10)
    INSERT INTO RegistrationEvidence (registration_id, evidence_type, file_url, uploaded_at, retention_expires_at)
    VALUES (10, 'IDENTITY_DOCUMENT', '/api/uploads/evidence/6/0000000000000000000000000000ab04.jpg',
            DATEADD(DAY, -12, SYSUTCDATETIME()), DATEADD(DAY, 718, SYSUTCDATETIME()));
IF NOT EXISTS (SELECT 1 FROM RegistrationEvidence WHERE registration_id = 2 AND evidence_type = 'IDENTITY_DOCUMENT_BACK')
    INSERT INTO RegistrationEvidence (registration_id, evidence_type, file_url, uploaded_at, retention_expires_at)
    VALUES (2, 'IDENTITY_DOCUMENT_BACK', '/api/uploads/evidence/6/0000000000000000000000000000ab04.jpg',
            DATEADD(DAY, -3, SYSUTCDATETIME()), DATEADD(DAY, 727, SYSUTCDATETIME()));
IF NOT EXISTS (SELECT 1 FROM RegistrationEvidence WHERE registration_id = 2 AND evidence_type = 'PORTRAIT_SELFIE')
    INSERT INTO RegistrationEvidence (registration_id, evidence_type, file_url, uploaded_at, retention_expires_at)
    VALUES (2, 'PORTRAIT_SELFIE', '/api/uploads/evidence/6/0000000000000000000000000000ab04.jpg',
            DATEADD(DAY, -3, SYSUTCDATETIME()), DATEADD(DAY, 727, SYSUTCDATETIME()));
GO

-- SIDE-11 / WARD-16: one proposal of each review state. A proposal must carry its
-- photo (CK_SidewalkSlots_ProposalCoherent) and takes the VP-{zone}-{timestamp} code
-- the API generates.
IF NOT EXISTS (SELECT 1 FROM SidewalkSlots WHERE slot_id = 28)
BEGIN
    SET IDENTITY_INSERT SidewalkSlots ON;
    INSERT INTO SidewalkSlots
        (slot_id, slot_code, zone_id, latitude, longitude, width_meters, length_meters,
         slot_status, source, proposed_by_registration_id, proposal_review_status,
         proposal_photo_url, proposal_reviewed_by, proposal_review_reason, created_at)
    VALUES
        (28, 'VP-1-20260101000000001', 1, 16.047310, 108.216400, 2.00, 2.50,
            'AVAILABLE', 'VENDOR_PROPOSED', 1, 'PENDING',
            '/api/uploads/evidence/5/0000000000000000000000000000ab01.jpg', NULL, NULL,
            DATEADD(DAY, -1, SYSUTCDATETIME())),
        (29, 'VP-1-20260101000000002', 1, 16.047330, 108.216550, 2.00, 2.50,
            'AVAILABLE', 'VENDOR_PROPOSED', 10, 'APPROVED',
            '/api/uploads/evidence/6/0000000000000000000000000000ab04.jpg', 2,
            N'Vị trí phù hợp quy hoạch, đủ 1,5 m lối đi bộ.',
            DATEADD(DAY, -9, SYSUTCDATETIME())),
        (30, 'VP-1-20260101000000003', 1, 16.047290, 108.214950, 2.00, 2.50,
            'AVAILABLE', 'VENDOR_PROPOSED', 10, 'REJECTED',
            '/api/uploads/evidence/6/0000000000000000000000000000ab04.jpg', 2,
            N'Vị trí nằm trong hành lang an toàn của trạm biến áp.',
            DATEADD(DAY, -15, SYSUTCDATETIME()));
    SET IDENTITY_INSERT SidewalkSlots OFF;
END;
GO

-- Holds on two NVL slots. Real holds last 15 minutes (SlotHolds:TtlMinutes); these use a
-- day so the demo still shows them after the seed has been loaded for a while.
IF NOT EXISTS (SELECT 1 FROM SlotHolds)
    INSERT INTO SlotHolds (slot_id, registration_id, held_at, expires_at) VALUES
        (5, 10, SYSUTCDATETIME(), DATEADD(DAY, 1, SYSUTCDATETIME())),
        (6,  1, SYSUTCDATETIME(), DATEADD(DAY, 1, SYSUTCDATETIME()));
GO

-- SIDE-12/13, WARD-18: contract 1 is on offer to vendor 2 (PENDING), contract 2 has been
-- accepted by vendor 2 and waits for the ward, and an older offer to vendor 4 was
-- rejected because vendor 4 has no approved registration (BR-26).
IF NOT EXISTS (SELECT 1 FROM SlotTransferRequests)
BEGIN
    SET IDENTITY_INSERT SlotTransferRequests ON;
    INSERT INTO SlotTransferRequests
        (transfer_id, contract_id, from_vendor_id, to_vendor_id, transfer_status, initiated_at,
         accepted_at, reviewed_by, review_decision_reason, reviewed_at)
    VALUES
        (1, 1, 1, 2, 'PENDING',              DATEADD(DAY,  -1, SYSUTCDATETIME()), NULL, NULL, NULL, NULL),
        (2, 2, 1, 2, 'ACCEPTED_BY_RECEIVER', DATEADD(DAY,  -3, SYSUTCDATETIME()), DATEADD(DAY, -2, SYSUTCDATETIME()), NULL, NULL, NULL),
        (3, 1, 1, 4, 'REJECTED',             DATEADD(DAY, -20, SYSUTCDATETIME()), DATEADD(DAY, -19, SYSUTCDATETIME()), 2,
            N'Người nhận chưa có hồ sơ đăng ký được duyệt (BR-26).', DATEADD(DAY, -18, SYSUTCDATETIME()));
    SET IDENTITY_INSERT SlotTransferRequests OFF;
END;
GO

-- SIDE-06 / WARD-09: an open renewal on contract 1, and a past rejected one on contract 2.
IF NOT EXISTS (SELECT 1 FROM RenewalRequests)
BEGIN
    SET IDENTITY_INSERT RenewalRequests ON;
    INSERT INTO RenewalRequests
        (renewal_id, contract_id, requested_term_days, renewal_status, new_end_date,
         reviewed_by, review_decision_reason, reviewed_at, created_at)
    VALUES
        (1, 1, 90, 'PENDING',  NULL, NULL, NULL, NULL, DATEADD(DAY, -1, SYSUTCDATETIME())),
        (2, 2, 60, 'REJECTED', NULL, 2,
            N'Hợp đồng còn hơn 150 ngày, chưa đến thời điểm gia hạn.',
            DATEADD(DAY, -8, SYSUTCDATETIME()), DATEADD(DAY, -9, SYSUTCDATETIME()));
    SET IDENTITY_INSERT RenewalRequests OFF;
END;
GO

-- SIDE-09/10 / WARD-17: two vendors ask for the same slot next to a new address (HD-01), so
-- the ward has a conflict to settle. The second one has already been queued (UNDER_REVIEW).
IF NOT EXISTS (SELECT 1 FROM AddressChangeRequests)
BEGIN
    SET IDENTITY_INSERT AddressChangeRequests ON;
    INSERT INTO AddressChangeRequests
        (address_change_id, registration_id, new_address, new_latitude, new_longitude,
         released_contract_id, requested_new_slot_id, change_status, conflict_resolution_note,
         reviewed_by, reviewed_at, created_at)
    VALUES
        (1, 1, N'200 Hoàng Diệu, Phường Hải Châu 1, Đà Nẵng', 16.069500, 108.222100,
            1, 21, 'PENDING', NULL, NULL, NULL, DATEADD(DAY, -2, SYSUTCDATETIME())),
        (2, 9, N'204 Hoàng Diệu, Phường Hải Châu 1, Đà Nẵng', 16.069520, 108.222130,
            2, 21, 'UNDER_REVIEW', N'Xếp hàng chờ theo thứ tự nộp hồ sơ, ưu tiên hồ sơ nộp trước.',
            2, DATEADD(DAY, -1, SYSUTCDATETIME()), DATEADD(DAY, -1, SYSUTCDATETIME()));
    SET IDENTITY_INSERT AddressChangeRequests OFF;
END;
GO


/* ============================================================
   9. WARD COMPLIANCE (WARD-11, 12, 13, FEE-04, FEE-05)
   Scans, violations and penalties in every state: awaiting sanction, unpaid,
   paid, recorded against an unidentified occupier, and raised from a customer
   report. Fictional data throughout.
   ============================================================ */

-- Customer and community reports (BUY-05). Report 2 became violation 4.
IF NOT EXISTS (SELECT 1 FROM VendorReports)
BEGIN
    SET IDENTITY_INSERT VendorReports ON;
    INSERT INTO VendorReports
        (report_id, reporter_user_id, vendor_id, slot_id, scanned_permit_id, report_reason,
         report_status, reviewed_by, reviewed_at, created_at)
    VALUES
        (1, 9,  1,  NULL, NULL, N'Ô bày hàng lấn ra lối đi bộ vào giờ cao điểm buổi sáng.',
            'PENDING', NULL, NULL, DATEADD(DAY, -1, SYSUTCDATETIME())),
        (2, 10, NULL, 15, NULL, N'Có người bày bán tại ô NVL-15 nhưng không thấy giấy phép.',
            'REVIEWED', 2, DATEADD(DAY, -1, SYSUTCDATETIME()), DATEADD(DAY, -2, SYSUTCDATETIME())),
        (3, 9,  1,  NULL, 2, N'Nghi ngờ giấy phép giả.',
            'DISMISSED', 2, DATEADD(DAY, -6, SYSUTCDATETIME()), DATEADD(DAY, -7, SYSUTCDATETIME()));
    SET IDENTITY_INSERT VendorReports OFF;
END;
GO

-- WARD-11 / BUY-02 scan history. Scan 3 was taken about 95 m from the slot it belongs to,
-- the sort of drift AIC-06 is meant to flag. Scan 6 is a forged code that matched nothing.
IF NOT EXISTS (SELECT 1 FROM PermitScanLogs)
    INSERT INTO PermitScanLogs (permit_id, qr_payload, scanned_by, scan_context, scan_result, latitude, longitude, scanned_at)
    VALUES
        (1, 'SEED-PERMIT-CONTRACT-1-DO-NOT-SCAN', 2,    'WARD_INSPECTION', 'VALID', 16.047052, 108.214703, DATEADD(DAY, -20, SYSUTCDATETIME())),
        (1, 'SEED-PERMIT-CONTRACT-1-DO-NOT-SCAN', 2,    'WARD_INSPECTION', 'VALID', 16.047049, 108.214698, DATEADD(DAY,  -9, SYSUTCDATETIME())),
        (1, 'SEED-PERMIT-CONTRACT-1-DO-NOT-SCAN', 2,    'WARD_INSPECTION', 'VALID', 16.047900, 108.214700, DATEADD(DAY,  -2, SYSUTCDATETIME())),
        (2, 'SEED-PERMIT-CONTRACT-2-DO-NOT-SCAN', 2,    'WARD_INSPECTION', 'VALID', 16.047143, 108.215678, DATEADD(DAY,  -5, SYSUTCDATETIME())),
        (2, 'SEED-PERMIT-CONTRACT-2-DO-NOT-SCAN', 9,    'PUBLIC_CHECK',    'VALID', NULL,      NULL,       DATEADD(DAY,  -1, SYSUTCDATETIME())),
        (NULL, 'FORGED-QR-DEMO-0001',             NULL, 'PUBLIC_CHECK',    'NOT_FOUND', NULL,  NULL,       DATEADD(DAY,  -1, SYSUTCDATETIME()));
GO

-- WARD-12 violations. 1 awaits a sanction, 2 has an unpaid penalty, 3 and 4 have no identified
-- offender (a penalty cannot exist yet), 5 has a paid penalty.
IF NOT EXISTS (SELECT 1 FROM Violations)
BEGIN
    SET IDENTITY_INSERT Violations ON;
    INSERT INTO Violations
        (violation_id, contract_id, slot_id, vendor_id, violation_type, description,
         recorded_by, source, source_report_id, recorded_at)
    VALUES
        (1, 1,    1,  1,    'OUTSIDE_SLOT',     N'Bày bán vượt ra ngoài ô khoảng 1 m.',
            2, 'ON_SITE',         NULL, DATEADD(DAY,  -4, SYSUTCDATETIME())),
        (2, 2,    8,  1,    'BLOCK_PEDESTRIAN', N'Xe đẩy và bàn ghế chiếm hết lối đi bộ.',
            2, 'ON_SITE',         NULL, DATEADD(DAY, -12, SYSUTCDATETIME())),
        (3, NULL, 12, NULL, 'NO_PERMIT',        N'Có người bày bán tại ô NVL-12 khi ô chưa cho thuê.',
            2, 'ON_SITE',         NULL, DATEADD(DAY,  -3, SYSUTCDATETIME())),
        (4, NULL, 15, NULL, 'NO_PERMIT',        N'Xác minh theo phản ánh của người dân về ô NVL-15.',
            2, 'CUSTOMER_REPORT', 2,    DATEADD(DAY,  -1, SYSUTCDATETIME())),
        (5, 1,    1,  1,    'OUTSIDE_HOURS',    N'Bán ngoài khung giờ cho phép của khu vực.',
            2, 'ON_SITE',         NULL, DATEADD(DAY, -35, SYSUTCDATETIME()));
    SET IDENTITY_INSERT Violations OFF;
END;
GO

-- WARD-13 penalties. The amount is the ward's rate at the time, frozen on the row (BR-33);
-- the signer comes from the officer's account, as the sanction workflow does.
IF NOT EXISTS (SELECT 1 FROM Penalties)
BEGIN
    DECLARE @signer NVARCHAR(150) = (SELECT full_name FROM UserAccounts WHERE user_id = 2);
    DECLARE @title  NVARCHAR(100) = (SELECT sanction_authority_title FROM UserAccounts WHERE user_id = 2);

    SET IDENTITY_INSERT Penalties ON;
    INSERT INTO Penalties
        (penalty_id, violation_id, penalty_schedule_id, amount, penalty_status, created_at, paid_at,
         decision_number, signer_name, signer_title)
    SELECT 1, 2, s.penalty_schedule_id, s.penalty_amount, 'UNPAID', DATEADD(DAY, -11, SYSUTCDATETIME()), NULL,
           N'QĐXP-2026/0001', @signer, @title
    FROM PenaltyFeeSchedules s
    WHERE s.ward_unit_id = 10 AND s.violation_type = 'BLOCK_PEDESTRIAN' AND s.effective_to IS NULL;

    INSERT INTO Penalties
        (penalty_id, violation_id, penalty_schedule_id, amount, penalty_status, created_at, paid_at,
         decision_number, signer_name, signer_title)
    SELECT 2, 5, s.penalty_schedule_id, s.penalty_amount, 'PAID', DATEADD(DAY, -34, SYSUTCDATETIME()),
           DATEADD(DAY, -33, SYSUTCDATETIME()), N'QĐXP-2026/0002', @signer, @title
    FROM PenaltyFeeSchedules s
    WHERE s.ward_unit_id = 10 AND s.violation_type = 'OUTSIDE_HOURS' AND s.effective_to IS NULL;
    SET IDENTITY_INSERT Penalties OFF;
END;
GO


/* ============================================================
   10. BUSINESS REGISTRATION: MAU SO 01 AND eKYC (REG-01/02, WARD-04/05)
   Registrations 1, 2, 4, 9 and 10 carry the owner-identity fields. Approved ones
   (1, 9, 10) were confirmed by an officer against the physical CCCD; 2 and 4 were
   not, so the ward still has to confirm identity before it can approve them.
   Registration 3 is left blank on purpose to show an incomplete form. The ID
   numbers are fictional.
   ============================================================ */

UPDATE BusinessRegistrations SET
    id_type = 'CCCD', id_number = '048185000001', id_issued_date = '2021-05-20',
    id_issued_place = N'Cục Cảnh sát QLHC về TTXH', owner_date_of_birth = '1985-03-12',
    owner_gender = N'Nữ', owner_ethnicity = N'Kinh',
    permanent_address = N'123 Nguyễn Văn Linh, Phường Hải Châu 1, Đà Nẵng',
    contact_address = N'123 Nguyễn Văn Linh, Phường Hải Châu 1, Đà Nẵng',
    business_line = N'Bán lẻ bánh mì, xôi ăn sáng', business_line_code = '5610',
    capital_amount = 20000000, labor_count = 2, planned_start_date = '2026-08-01',
    biometric_consent_at = DATEADD(DAY, -32, SYSUTCDATETIME()),
    food_safety_commitment_at = DATEADD(DAY, -32, SYSUTCDATETIME()),
    identity_verified_by = 2, identity_verified_at = DATEADD(DAY, -28, SYSUTCDATETIME()),
    identity_verification_note = N'Đã đối chiếu CCCD gắn chip với người đến làm việc.'
WHERE registration_id = 1 AND id_number IS NULL;

UPDATE BusinessRegistrations SET
    id_type = 'CCCD', id_number = '048185000001', id_issued_date = '2021-05-20',
    id_issued_place = N'Cục Cảnh sát QLHC về TTXH', owner_date_of_birth = '1985-03-12',
    owner_gender = N'Nữ', owner_ethnicity = N'Kinh',
    permanent_address = N'123 Nguyễn Văn Linh, Phường Hải Châu 1, Đà Nẵng',
    contact_address = N'45 Nguyễn Văn Linh, Phường Hải Châu 1, Đà Nẵng',
    business_line = N'Bún chả, nem cua bể', business_line_code = '5610',
    capital_amount = 35000000, labor_count = 3, planned_start_date = '2026-09-05',
    biometric_consent_at = DATEADD(DAY, -15, SYSUTCDATETIME()),
    food_safety_commitment_at = DATEADD(DAY, -15, SYSUTCDATETIME()),
    identity_verified_by = 2, identity_verified_at = DATEADD(DAY, -11, SYSUTCDATETIME()),
    identity_verification_note = N'Chủ hộ đã xác minh ở hồ sơ trước, đối chiếu lại CCCD.'
WHERE registration_id = 9 AND id_number IS NULL;

UPDATE BusinessRegistrations SET
    id_type = 'CCCD', id_number = '048079000002', id_issued_date = '2022-01-15',
    id_issued_place = N'Cục Cảnh sát QLHC về TTXH', owner_date_of_birth = '1979-11-02',
    owner_gender = N'Nam', owner_ethnicity = N'Kinh',
    permanent_address = N'56 Lê Duẩn, Phường Hải Châu 1, Đà Nẵng',
    contact_address = N'56 Lê Duẩn, Phường Hải Châu 1, Đà Nẵng',
    business_line = N'Bán nước mía, nước ép trái cây', business_line_code = '5630',
    capital_amount = 8000000, labor_count = 1, planned_start_date = '2026-10-01',
    biometric_consent_at = DATEADD(DAY, -3, SYSUTCDATETIME()),
    food_safety_commitment_at = DATEADD(DAY, -3, SYSUTCDATETIME())
WHERE registration_id = 2 AND id_number IS NULL;

UPDATE BusinessRegistrations SET
    id_type = 'CCCD', id_number = '048079000002', id_issued_date = '2022-01-15',
    id_issued_place = N'Cục Cảnh sát QLHC về TTXH', owner_date_of_birth = '1979-11-02',
    owner_gender = N'Nam', owner_ethnicity = N'Kinh',
    permanent_address = N'56 Lê Duẩn, Phường Hải Châu 1, Đà Nẵng',
    contact_address = N'56 Lê Duẩn, Phường Hải Châu 1, Đà Nẵng',
    business_line = N'Bán nước ép trái cây lưu động', business_line_code = '5630',
    capital_amount = 6000000, labor_count = 1, planned_start_date = '2026-09-15',
    biometric_consent_at = DATEADD(DAY, -12, SYSUTCDATETIME()),
    food_safety_commitment_at = DATEADD(DAY, -12, SYSUTCDATETIME()),
    identity_verified_by = 2, identity_verified_at = DATEADD(DAY, -8, SYSUTCDATETIME()),
    identity_verification_note = N'Đã đối chiếu CCCD với người đến làm việc.'
WHERE registration_id = 10 AND id_number IS NULL;

UPDATE BusinessRegistrations SET
    id_type = 'CCCD', id_number = '048090000004', id_issued_date = '2021-09-09',
    id_issued_place = N'Cục Cảnh sát QLHC về TTXH', owner_date_of_birth = '1990-07-25',
    owner_gender = N'Nam', owner_ethnicity = N'Kinh',
    permanent_address = N'9 Trần Phú, Phường Hải Châu 1, Đà Nẵng',
    contact_address = N'9 Trần Phú, Phường Hải Châu 1, Đà Nẵng',
    business_line = N'Bán bún, phở gánh', business_line_code = '5610',
    capital_amount = 5000000, labor_count = 1, planned_start_date = '2026-10-10',
    biometric_consent_at = DATEADD(DAY, -5, SYSUTCDATETIME()),
    food_safety_commitment_at = DATEADD(DAY, -5, SYSUTCDATETIME())
WHERE registration_id = 4 AND id_number IS NULL;
GO

-- Mau so 01 "Thanh vien ho gia dinh cung gop von" for registration 1.
IF NOT EXISTS (SELECT 1 FROM BusinessRegistrationHouseholdMembers)
    INSERT INTO BusinessRegistrationHouseholdMembers
        (registration_id, full_name, date_of_birth, id_number, relationship_to_owner, capital_contribution)
    VALUES
        (1, N'Phạm Văn Hùng', '1983-06-18', '048083000011', N'Chồng', 8000000),
        (1, N'Phạm Thị Bích',  '2003-01-09', '048303000012', N'Con',    2000000);
GO

-- Recorded outcomes of the AI checks (decision support only, BR-41). Registration 2 matches;
-- registration 4 does not, which the ward screen should flag without deciding anything.
IF NOT EXISTS (SELECT 1 FROM KycVerificationResults)
    INSERT INTO KycVerificationResults
        (user_id, registration_id, check_type, provider, is_match, similarity_percent,
         confidence_percent, extracted_id_number, warnings, created_at)
    VALUES
        (6, 2, 'ID_CARD_OCR', 'FPT.AI', NULL, NULL, 92, '048079000002', NULL,
            DATEADD(DAY, -3, SYSUTCDATETIME())),
        (6, 2, 'FACE_MATCH',  'FPT.AI', 1, 87.50, NULL, NULL, NULL,
            DATEADD(DAY, -3, SYSUTCDATETIME())),
        (8, 4, 'ID_CARD_OCR', 'FPT.AI', NULL, NULL, 78, '048090000004', N'Ảnh mặt sau hơi mờ.',
            DATEADD(DAY, -5, SYSUTCDATETIME())),
        (8, 4, 'FACE_MATCH',  'FPT.AI', 0, 41.20, NULL, NULL, N'Khuôn mặt không khớp ảnh trên thẻ.',
            DATEADD(DAY, -5, SYSUTCDATETIME()));
GO


/* ============================================================
   11. MODERATION, REVIEWS AND COMPLAINTS (BUY-04, REV-01/03/04, ADM-03..05)
   ============================================================ */

IF NOT EXISTS (SELECT 1 FROM VendorComments)
    INSERT INTO VendorComments (vendor_id, customer_user_id, rating, comment_text, created_at) VALUES
        (1, 9,  5, N'Bánh mì nóng giòn, chủ quán thân thiện.',                 DATEADD(DAY, -5, SYSUTCDATETIME())),
        (1, 10, 4, N'Xôi ngon nhưng hơi đông vào giờ cao điểm.',               DATEADD(DAY, -3, SYSUTCDATETIME()));
GO

-- REV-01: a review on the completed order 1 (only a COMPLETED order can be reviewed).
IF NOT EXISTS (SELECT 1 FROM Reviews)
    INSERT INTO Reviews (order_id, customer_user_id, rating, review_text, created_at)
    VALUES (1, 9, 5, N'Đồ ăn ngon, lấy hàng nhanh, đúng giờ hẹn.', DATEADD(DAY, -5, SYSUTCDATETIME()));
GO

-- REV-03 / ADM-03/04: two pending reports and one already dismissed.
IF NOT EXISTS (SELECT 1 FROM ReportedContent)
    INSERT INTO ReportedContent (content_type, content_id, reporter_user_id, reason, status, reviewed_by, reviewed_at, created_at)
    VALUES
        ('STOREFRONT', 2, 10, N'Mô tả gian hàng có nội dung quảng cáo không phù hợp.', 'PENDING',   NULL, NULL, DATEADD(DAY, -2, SYSUTCDATETIME())),
        ('MENU_ITEM',  6,  9, N'Món ghi còn hàng nhưng thực tế đã hết từ sáng.',        'PENDING',   NULL, NULL, DATEADD(DAY, -1, SYSUTCDATETIME())),
        ('REVIEW',     1, 10, N'Đánh giá có dấu hiệu spam.',                            'DISMISSED', 1, DATEADD(DAY, -3, SYSUTCDATETIME()), DATEADD(DAY, -4, SYSUTCDATETIME()));
GO

-- REV-04 / ADM-05: an open refund request, and a resolved complaint on the same order.
IF NOT EXISTS (SELECT 1 FROM Complaints)
    INSERT INTO Complaints
        (order_id, customer_user_id, complaint_type, description, requested_refund_amount,
         status, resolved_by, resolution_notes, created_at, resolved_at)
    VALUES
        (1, 9, 'REFUND_REQUEST', N'Đơn thiếu một ổ bánh mì so với hóa đơn, xin hoàn tiền phần thiếu.', 25000,
            'OPEN', NULL, NULL, DATEADD(DAY, -4, SYSUTCDATETIME()), NULL),
        (1, 9, 'COMPLAINT', N'Bánh mì hơi nguội khi nhận.', NULL,
            'RESOLVED', 1, N'Đã nhắc nhở cửa hàng giữ nóng đơn hàng.',
            DATEADD(DAY, -5, SYSUTCDATETIME()), DATEADD(DAY, -4, SYSUTCDATETIME()));
GO


/* ============================================================
   12. PAYMENTS AND REFUNDS (PAY-01..04, FEE-01, FEE-04, SYS-04)
   Every paid order, paid fee instalment and paid penalty has its successful
   transaction; order 4 still waits for payment; order 5 was rejected after
   payment, so it has an automatic refund. Provider references are fictional.
   ============================================================ */

IF NOT EXISTS (SELECT 1 FROM PaymentTransactions)
BEGIN
    SET IDENTITY_INSERT PaymentTransactions ON;
    INSERT INTO PaymentTransactions
        (transaction_id, idempotency_key, payment_purpose, fee_item_id, penalty_id, order_id,
         provider, provider_reference, amount, transaction_status, callback_received_at, created_at)
    VALUES
        (1, N'seed-order-1',   'ORDER',      NULL, NULL, 1, 'MOMO',    N'MOMO-SEED-0001', 60000,   'SUCCESS', DATEADD(DAY,  -6, SYSUTCDATETIME()), DATEADD(DAY,  -6, SYSUTCDATETIME())),
        (2, N'seed-order-2',   'ORDER',      NULL, NULL, 2, 'ZALOPAY', N'ZP-SEED-0002',   75000,   'SUCCESS', DATEADD(HOUR, -2, SYSUTCDATETIME()), DATEADD(HOUR, -2, SYSUTCDATETIME())),
        (3, N'seed-order-3',   'ORDER',      NULL, NULL, 3, 'MOMO',    N'MOMO-SEED-0003', 45000,   'SUCCESS', DATEADD(MINUTE,-24, SYSUTCDATETIME()), DATEADD(MINUTE,-25, SYSUTCDATETIME())),
        (4, N'seed-order-4',   'ORDER',      NULL, NULL, 4, 'MOMO',    NULL,              50000,   'PENDING', NULL,                                DATEADD(MINUTE, -5, SYSUTCDATETIME())),
        (5, N'seed-order-5',   'ORDER',      NULL, NULL, 5, 'ZALOPAY', N'ZP-SEED-0005',   25000,   'SUCCESS', DATEADD(DAY,  -2, SYSUTCDATETIME()), DATEADD(DAY,  -2, SYSUTCDATETIME())),
        (6, N'seed-fee-1',     'RENTAL_FEE', 1,    NULL, NULL, 'MOMO', N'MOMO-SEED-F001', 1540000, 'SUCCESS', DATEADD(DAY, -29, SYSUTCDATETIME()), DATEADD(DAY, -29, SYSUTCDATETIME())),
        (7, N'seed-fee-4',     'RENTAL_FEE', 4,    NULL, NULL, 'ZALOPAY', N'ZP-SEED-F004', 2630000, 'SUCCESS', DATEADD(DAY,  -9, SYSUTCDATETIME()), DATEADD(DAY,  -9, SYSUTCDATETIME())),
        (8, N'seed-penalty-2', 'PENALTY',    NULL, 2,    NULL, 'MOMO', N'MOMO-SEED-P002',  500000, 'SUCCESS', DATEADD(DAY, -33, SYSUTCDATETIME()), DATEADD(DAY, -33, SYSUTCDATETIME()));
    SET IDENTITY_INSERT PaymentTransactions OFF;
END;
GO

-- SYS-04: what the providers sent. The last event carries a bad signature and matched nothing.
IF NOT EXISTS (SELECT 1 FROM PaymentCallbackEvents)
    INSERT INTO PaymentCallbackEvents
        (provider, provider_reference, transaction_id, raw_payload, signature_valid, processing_result, received_at)
    VALUES
        ('MOMO',    N'MOMO-SEED-0001', 1, N'{"seed":true,"resultCode":0,"amount":60000}',   1, 'APPLIED',  DATEADD(DAY,  -6, SYSUTCDATETIME())),
        ('MOMO',    N'MOMO-SEED-F001', 6, N'{"seed":true,"resultCode":0,"amount":1540000}', 1, 'APPLIED',  DATEADD(DAY, -29, SYSUTCDATETIME())),
        ('MOMO',    N'MOMO-SEED-P002', 8, N'{"seed":true,"resultCode":0,"amount":500000}',  1, 'APPLIED',  DATEADD(DAY, -33, SYSUTCDATETIME())),
        ('MOMO',    N'MOMO-SEED-P002', 8, N'{"seed":true,"resultCode":0,"amount":500000}',  1, 'DUPLICATE', DATEADD(DAY, -33, SYSUTCDATETIME())),
        ('ZALOPAY', N'ZP-UNKNOWN-0001', NULL, N'{"seed":true,"note":"unsigned callback"}',  0, 'REJECTED', DATEADD(DAY,  -1, SYSUTCDATETIME()));
GO

-- PAY-04: order 5 was rejected after payment, so its money went back automatically.
IF NOT EXISTS (SELECT 1 FROM RefundTransactions)
    INSERT INTO RefundTransactions
        (order_id, payment_transaction_id, complaint_id, idempotency_key, amount, refund_reason,
         provider, provider_refund_reference, refund_status, requested_at, completed_at)
    VALUES
        (5, 5, NULL, N'seed-refund-order-5', 25000, 'ORDER_REJECTED', 'ZALOPAY', N'ZP-REFUND-SEED-0005',
            'SUCCESS', DATEADD(DAY, -2, SYSUTCDATETIME()), DATEADD(DAY, -2, SYSUTCDATETIME()));
GO

-- FEE-03: the invoice for the paid penalty (the fee invoices are in section 6).
IF NOT EXISTS (SELECT 1 FROM Invoices WHERE invoice_number = N'HD-2026-000003')
    INSERT INTO Invoices (invoice_number, fee_item_id, penalty_id, vendor_id, amount, issued_at)
    VALUES (N'HD-2026-000003', NULL, 2, 1, 500000, DATEADD(DAY, -33, SYSUTCDATETIME()));
GO


/* ============================================================
   13. NOTIFICATIONS AND AI LOG (SYS-06, AIC-xx)
   ============================================================ */

IF NOT EXISTS (SELECT 1 FROM Notifications)
    INSERT INTO Notifications (user_id, notification_type, title, body, related_entity_type, related_entity_id, is_read, sent_at)
    VALUES
        (5,  'PERMIT_ISSUED',            N'Giấy phép số đã được cấp',
            N'Giấy phép cho ô NVL-08 đã được cấp. Bạn có thể xem mã QR trong mục Ô thuê.', 'CONTRACT', 2, 1, DATEADD(DAY, -10, SYSUTCDATETIME())),
        (5,  'ENROLLMENT_STATUS_UPDATE', N'Hồ sơ đăng ký đã được duyệt',
            N'Hồ sơ "Bún chả Hải Châu" đã được Phường duyệt.',                              'REGISTRATION', 9, 1, DATEADD(DAY, -11, SYSUTCDATETIME())),
        (5,  'PENALTY_SANCTION_ISSUED',  N'Quyết định xử phạt mới',
            N'Bạn có một quyết định xử phạt QĐXP-2026/0001 chưa thanh toán.',               'PENALTY', 1, 0, DATEADD(DAY, -11, SYSUTCDATETIME())),
        (5,  'ORDER',                    N'Có đơn hàng mới',
            N'Đơn DH-2026-0004 đang chờ khách thanh toán.',                                 'ORDER', 4, 0, DATEADD(MINUTE, -5, SYSUTCDATETIME())),
        (6,  'ENROLLMENT_STATUS_UPDATE', N'Hồ sơ đang chờ duyệt',
            N'Hồ sơ "Xe nước mía Tám Ù" đã được gửi tới Phường Hải Châu 1.',                'REGISTRATION', 2, 0, DATEADD(DAY, -3, SYSUTCDATETIME())),
        (9,  'ORDER',                    N'Đơn hàng đã hoàn tất',
            N'Đơn DH-2026-0001 đã được nhận. Bạn có thể đánh giá cửa hàng.',                'ORDER', 1, 1, DATEADD(DAY, -6, SYSUTCDATETIME())),
        (10, 'ORDER',                    N'Đơn hàng sẵn sàng lấy',
            N'Đơn DH-2026-0002 đã sẵn sàng tại quầy Bún chả Hải Châu.',                     'ORDER', 2, 0, DATEADD(HOUR, -1, SYSUTCDATETIME())),
        (2,  'WARD_REVIEW',              N'Có hồ sơ mới cần duyệt',
            N'Hồ sơ "Xe nước mía Tám Ù" đang chờ Phường xem xét.',                          'REGISTRATION', 2, 0, DATEADD(DAY, -3, SYSUTCDATETIME()));
GO

-- BR-41: AI output is only ever a suggestion; nothing here has been accepted or overridden yet.
IF NOT EXISTS (SELECT 1 FROM AIAssistanceLogs)
    INSERT INTO AIAssistanceLogs (feature_code, entity_type, entity_id, ai_output, confidence, reviewed_by, accepted, created_at)
    VALUES
        ('AIC-01', 'BusinessRegistration', 2,
            N'{"seed":true,"idNumber":"048079000002","fullName":"Hoàng Văn Tám","dateOfBirth":"1979-11-02"}',
            92.00, NULL, NULL, DATEADD(DAY, -3, SYSUTCDATETIME())),
        ('AIC-04', 'SidewalkSlot', 28,
            N'{"seed":true,"estimatedSidewalkWidthMeters":2.4,"obstructionLevel":"LOW","note":"Cần khảo sát thực địa trước khi duyệt."}',
            71.50, NULL, NULL, DATEADD(DAY, -1, SYSUTCDATETIME()));
GO


/* ============================================================
   Summary
   ============================================================ */
SELECT
    (SELECT COUNT(*) FROM UserAccounts)           AS [accounts],
    (SELECT COUNT(*) FROM Vendors)                AS [vendors],
    (SELECT COUNT(*) FROM PricingZones)           AS [zones],
    (SELECT COUNT(*) FROM SidewalkSlots)          AS [slots],
    (SELECT COUNT(*) FROM BusinessRegistrations)  AS [registrations],
    (SELECT COUNT(*) FROM RegistrationEvidence)   AS [evidence],
    (SELECT COUNT(*) FROM RentalContracts)        AS [contracts],
    (SELECT COUNT(*) FROM DigitalPermits)         AS [permits],
    (SELECT COUNT(*) FROM FeeScheduleItems)       AS [fee_items],
    (SELECT COUNT(*) FROM Storefronts)            AS [storefronts],
    (SELECT COUNT(*) FROM MenuItems)              AS [menu_items],
    (SELECT COUNT(*) FROM Orders)                 AS [orders],
    (SELECT COUNT(*) FROM SlotTransferRequests)   AS [transfers],
    (SELECT COUNT(*) FROM Violations)             AS [violations],
    (SELECT COUNT(*) FROM Penalties)              AS [penalties],
    (SELECT COUNT(*) FROM PaymentTransactions)    AS [payments],
    (SELECT COUNT(*) FROM Notifications)          AS [notifications];

SELECT registration_status, COUNT(*) AS [count]
FROM BusinessRegistrations GROUP BY registration_status ORDER BY registration_status;
GO

SET NOEXEC OFF;
GO
