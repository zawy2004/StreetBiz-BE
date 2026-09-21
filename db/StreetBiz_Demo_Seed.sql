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
    (SELECT COUNT(*) FROM Orders)                 AS [orders];

SELECT registration_status, COUNT(*) AS [count]
FROM BusinessRegistrations GROUP BY registration_status ORDER BY registration_status;
GO

SET NOEXEC OFF;
GO
