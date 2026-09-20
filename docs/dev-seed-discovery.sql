-- Customer discovery (DISC-01..06) fixture for local development.
--
-- Discovery lists storefronts, and a storefront exists only after STORE-01, which
-- the backend does not implement yet. This script creates the storefronts, their
-- opening hours and their menus directly, so the marketplace endpoints and the
-- customer "Khám phá" / "Tìm kiếm" screens can be exercised end to end.
--
-- Idempotent: safe to run more than once. It only ever ADDS rows that are missing
-- (a storefront per eligible registration, hours for a storefront that has none,
-- menu items that are not on the menu yet) and never edits or removes anything.
--
-- Prerequisite: registrations that pass the Phase 2 gate, i.e. an APPROVED
-- BusinessRegistration with an ACTIVE RentalContract (see docs/dev-seed-side.sql).
-- The first two such registrations, by id, become storefronts. FoodCategories come
-- from db/StreetBiz_SQL_Server_Data.sql; missing ones are skipped, not created.
--
-- Run (Docker):
--   docker cp BE/docs/dev-seed-discovery.sql mssql:/tmp/
--   MSYS_NO_PATHCONV=1 docker exec mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P <pwd> -C \
--       -d StreetBizDB -f 65001 -i /tmp/dev-seed-discovery.sql

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @eligible TABLE (n INT PRIMARY KEY, registration_id BIGINT, contract_id BIGINT);
INSERT INTO @eligible (n, registration_id, contract_id)
SELECT TOP (2)
       ROW_NUMBER() OVER (ORDER BY r.registration_id), r.registration_id, c.contract_id
FROM BusinessRegistrations r
JOIN RentalApplications a ON a.registration_id = r.registration_id
JOIN RentalContracts c    ON c.application_id = a.application_id AND c.vendor_id = r.vendor_id
WHERE r.registration_status = 'APPROVED'
  AND c.contract_status = 'ACTIVE'
GROUP BY r.registration_id, c.contract_id
ORDER BY r.registration_id;

-- 1) Storefronts: 1 = breakfast stall (bánh mì / xôi), 2 = lunch-and-evening noodle shop.
INSERT INTO Storefronts (registration_id, contract_id, storefront_name, description, availability_status)
SELECT e.registration_id, e.contract_id,
       CASE e.n WHEN 1 THEN N'Bánh mì & Xôi Cô Lan' ELSE N'Bún chả Hải Châu' END,
       CASE e.n WHEN 1 THEN N'Bánh mì nướng giòn, xôi gà xé nóng hổi mỗi sáng.'
                ELSE N'Bún chả nướng than, cơm gà xối mỡ và chè giải nhiệt.' END,
       'OPEN'
FROM @eligible e
WHERE NOT EXISTS (SELECT 1 FROM Storefronts s WHERE s.registration_id = e.registration_id);

-- 2) Opening hours (ISO weekdays: 1 = Monday ... 7 = Sunday), only for storefronts with none.
INSERT INTO StorefrontBusinessHours (storefront_id, day_of_week, opens_at, closes_at)
SELECT s.storefront_id, d.day_of_week,
       CASE e.n WHEN 1 THEN '05:30' ELSE '10:00' END,
       CASE e.n WHEN 1 THEN '11:00' ELSE '21:00' END
FROM @eligible e
JOIN Storefronts s ON s.registration_id = e.registration_id
JOIN (VALUES (1),(2),(3),(4),(5),(6),(7)) AS d(day_of_week)
  ON (e.n = 2 OR d.day_of_week <= 6)            -- the breakfast stall rests on Sundays
WHERE NOT EXISTS (SELECT 1 FROM StorefrontBusinessHours h WHERE h.storefront_id = s.storefront_id);

-- 3) Menus.
DECLARE @menu TABLE (n INT, category_name NVARCHAR(100), item_name NVARCHAR(180),
                     description NVARCHAR(500), unit_price DECIMAL(18,0), availability_status NVARCHAR(20));
INSERT INTO @menu VALUES
 (1, N'Bánh mì',          N'Bánh mì thịt nguội',   N'Pate, thịt nguội, dưa leo, ngò.',        25000, 'AVAILABLE'),
 (1, N'Bánh mì',          N'Bánh mì trứng ốp la',  N'Trứng ốp la, xì dầu, hành phi.',         20000, 'AVAILABLE'),
 (1, N'Ăn vặt',           N'Xôi gà xé',            N'Xôi nếp dẻo, gà xé, hành phi.',          30000, 'AVAILABLE'),
 (1, N'Đồ uống',          N'Cà phê sữa đá',        N'Cà phê phin pha sữa đặc.',               18000, 'SOLD_OUT'),
 (1, N'Đồ uống',          N'Trà đá',               NULL,                                       5000, 'AVAILABLE'),
 (2, N'Bún - Phở - Mì',   N'Bún chả',              N'Chả nướng than hoa, bún, rau sống.',     45000, 'AVAILABLE'),
 (2, N'Bún - Phở - Mì',   N'Bún đậu mắm tôm',      N'Đậu chiên, chả cốm, mắm tôm.',           40000, 'AVAILABLE'),
 (2, N'Cơm',              N'Cơm gà xối mỡ',        N'Đùi gà giòn, cơm chiên, dưa chua.',      42000, 'SOLD_OUT'),
 (2, N'Chè - Tráng miệng', N'Chè khúc bạch',       N'Khúc bạch, hạnh nhân, vải thiều.',       25000, 'AVAILABLE'),
 (2, N'Đồ uống',          N'Nước mía',             N'Nước mía ép tươi, thêm tắc.',            12000, 'AVAILABLE');

INSERT INTO MenuItems (storefront_id, category_id, item_name, description, unit_price, availability_status)
SELECT s.storefront_id, c.category_id, m.item_name, m.description, m.unit_price, m.availability_status
FROM @menu m
JOIN @eligible e        ON e.n = m.n
JOIN Storefronts s      ON s.registration_id = e.registration_id
JOIN FoodCategories c   ON c.category_name = m.category_name
WHERE NOT EXISTS (SELECT 1 FROM MenuItems x WHERE x.storefront_id = s.storefront_id AND x.item_name = m.item_name);

SELECT s.storefront_id, s.storefront_name, s.availability_status,
       (SELECT COUNT(*) FROM StorefrontBusinessHours h WHERE h.storefront_id = s.storefront_id) AS hour_rows,
       (SELECT COUNT(*) FROM MenuItems x WHERE x.storefront_id = s.storefront_id) AS menu_items
FROM Storefronts s
ORDER BY s.storefront_id;
