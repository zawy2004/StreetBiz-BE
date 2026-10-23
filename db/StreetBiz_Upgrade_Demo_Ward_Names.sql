/* ============================================================
   StreetBiz — one-off upgrade: ward names in the demo rows
   ------------------------------------------------------------
   Development databases only, after StreetBiz_Upgrade_DaNang_94_Wards.sql,
   on a database that has the demo seed from before October 2026. Those
   demo rows spell out pre-2025 ward names in officers' sanction titles,
   registration addresses, address-change requests and notifications.
   This writes the current names, as db/StreetBiz_Demo_Seed.sql does now:
     Phường Hải Châu 1     -> Phường Hải Châu
     Phường Thanh Khê Đông -> Phường Thanh Khê
     Phường An Hải Bắc     -> Phường An Hải
   Never run it on a pilot database: there an address is what the vendor
   declared and stays as declared.

   One transaction: on any error nothing changes. Safe to run twice.
     sqlcmd -S "(localdb)\MSSQLLocalDB" -d StreetBizDB -E -I -f 65001 -i db\StreetBiz_Upgrade_Demo_Ward_Names.sql
   ============================================================ */
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;   -- the filtered indexes on the tables below need it
SET XACT_ABORT ON;
GO

-- Checked in a batch of its own, so it can say why: a missing column stops the next batch
-- from compiling at all, and a missing table makes it return before changing anything.
IF COL_LENGTH(N'dbo.UserAccounts', N'sanction_authority_title') IS NULL
   OR COL_LENGTH(N'dbo.BusinessRegistrations', N'contact_address') IS NULL
   OR COL_LENGTH(N'dbo.AddressChangeRequests', N'new_address') IS NULL
   OR COL_LENGTH(N'dbo.Notifications', N'body') IS NULL
    THROW 50001, N'This database is older than the current schema: rebuild it with scripts/setup-local-db.ps1 -Recreate. Nothing changed.', 1;
GO

IF OBJECT_ID(N'dbo.UserAccounts') IS NULL OR OBJECT_ID(N'dbo.BusinessRegistrations') IS NULL
   OR OBJECT_ID(N'dbo.AddressChangeRequests') IS NULL OR OBJECT_ID(N'dbo.Notifications') IS NULL
    RETURN;  -- reported by the batch above
-- Read in the wrong encoding, the names below would match nothing and the counts would still say 0.
IF CAST(N'Phường Hải Châu' AS VARBINARY(64)) <> CAST(CONCAT(N'Ph', NCHAR(432), NCHAR(7901), N'ng H', NCHAR(7843), N'i Ch', NCHAR(226), N'u') AS VARBINARY(64))
    THROW 50000, N'This file was not read as UTF-8: run sqlcmd with -f 65001. Nothing changed.', 1;

BEGIN TRAN;

DECLARE @names TABLE (old_name NVARCHAR(100), new_name NVARCHAR(100));
INSERT INTO @names VALUES
    (N'Phường Hải Châu 1', N'Phường Hải Châu'),
    (N'Phường Thanh Khê Đông', N'Phường Thanh Khê'),
    (N'Phường An Hải Bắc', N'Phường An Hải');

DECLARE @old NVARCHAR(100), @new NVARCHAR(100);
DECLARE names CURSOR LOCAL FAST_FORWARD FOR SELECT old_name, new_name FROM @names;
OPEN names;
FETCH NEXT FROM names INTO @old, @new;
WHILE @@FETCH_STATUS = 0
BEGIN
    UPDATE UserAccounts SET sanction_authority_title = REPLACE(sanction_authority_title, @old, @new)
        WHERE sanction_authority_title LIKE N'%' + @old + N'%';
    UPDATE BusinessRegistrations SET
        declared_address = REPLACE(declared_address, @old, @new),
        permanent_address = REPLACE(permanent_address, @old, @new),
        contact_address = REPLACE(contact_address, @old, @new)
        WHERE declared_address LIKE N'%' + @old + N'%' OR permanent_address LIKE N'%' + @old + N'%' OR contact_address LIKE N'%' + @old + N'%';
    UPDATE AddressChangeRequests SET new_address = REPLACE(new_address, @old, @new) WHERE new_address LIKE N'%' + @old + N'%';
    UPDATE Notifications SET body = REPLACE(body, @old, @new) WHERE body LIKE N'%' + @old + N'%';
    FETCH NEXT FROM names INTO @old, @new;
END;
CLOSE names;
DEALLOCATE names;

COMMIT;

-- All zero when done.
SELECT
    (SELECT COUNT(*) FROM UserAccounts t WHERE EXISTS (SELECT 1 FROM @names n
        WHERE t.sanction_authority_title LIKE N'%' + n.old_name + N'%')) AS accounts_left,
    (SELECT COUNT(*) FROM BusinessRegistrations t WHERE EXISTS (SELECT 1 FROM @names n
        WHERE t.declared_address LIKE N'%' + n.old_name + N'%' OR t.permanent_address LIKE N'%' + n.old_name + N'%'
           OR t.contact_address LIKE N'%' + n.old_name + N'%')) AS registrations_left,
    (SELECT COUNT(*) FROM AddressChangeRequests t WHERE EXISTS (SELECT 1 FROM @names n
        WHERE t.new_address LIKE N'%' + n.old_name + N'%')) AS address_changes_left,
    (SELECT COUNT(*) FROM Notifications t WHERE EXISTS (SELECT 1 FROM @names n
        WHERE t.body LIKE N'%' + n.old_name + N'%')) AS notifications_left;
