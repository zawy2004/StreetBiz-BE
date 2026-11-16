/* ============================================================
   StreetBiz — one-off upgrade: guests may report a vendor (BUY-05)
   ------------------------------------------------------------
   A person without an account may report a suspicious vendor. Such a
   report has no reporter, so VendorReports.reporter_user_id becomes
   NULL-able. The one-pending-report-per-person index skips reports
   without a reporter: a unique index treats NULLs as equal, so two
   guests reporting the same stall would otherwise block each other.

   Any database (development, pilot, production), before the API
   version that accepts guest reports. Existing rows are untouched.
   One transaction: on any error nothing changes. Safe to run twice.
     sqlcmd -S "(localdb)\MSSQLLocalDB" -d StreetBizDB -E -I -i db\StreetBiz_Upgrade_Guest_Vendor_Reports.sql
   ============================================================ */
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;   -- the filtered index below needs it
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.VendorReports') IS NULL
    THROW 50001, N'VendorReports does not exist: this is not a StreetBiz database. Nothing changed.', 1;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.VendorReports') AND name = N'reporter_user_id' AND is_nullable = 0)
BEGIN
    BEGIN TRAN;

    -- The column cannot change while an index or a foreign key depends on it.
    DROP INDEX UQ_VendorReports_OnePendingPerReporter ON dbo.VendorReports;
    DROP INDEX IX_VendorReports_Reporter ON dbo.VendorReports;
    ALTER TABLE dbo.VendorReports DROP CONSTRAINT FK_VendorReports_Reporter;

    ALTER TABLE dbo.VendorReports ALTER COLUMN reporter_user_id BIGINT NULL;

    ALTER TABLE dbo.VendorReports ADD CONSTRAINT FK_VendorReports_Reporter
        FOREIGN KEY (reporter_user_id) REFERENCES dbo.UserAccounts(user_id);
    CREATE INDEX IX_VendorReports_Reporter ON dbo.VendorReports(reporter_user_id, created_at);
    CREATE UNIQUE INDEX UQ_VendorReports_OnePendingPerReporter
        ON dbo.VendorReports(reporter_user_id, vendor_id)
        WHERE report_status = 'PENDING' AND vendor_id IS NOT NULL AND reporter_user_id IS NOT NULL;

    COMMIT;
    PRINT N'VendorReports.reporter_user_id is now NULL-able (guest reports).';
END
ELSE
    PRINT N'Already upgraded: nothing to do.';
GO
