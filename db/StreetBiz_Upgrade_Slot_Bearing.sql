/* ============================================================
   StreetBiz — one-off upgrade: a slot's orientation (3D slot map)
   ------------------------------------------------------------
   Adds SidewalkSlots.bearing_degrees: the compass bearing (0 = north,
   clockwise) of the slot's length side, the side that runs along the
   street. A rectangle turned half a circle covers the same ground, so
   the value is kept in [0, 180). NULL = not set: the map lays the slot
   along the street fitted through its zone, as before.

   Any database (development, pilot, production), before the API
   version that reads the column. Existing rows are untouched (NULL).
   One transaction: on any error nothing changes. Safe to run twice.
     sqlcmd -S "(localdb)\MSSQLLocalDB" -d StreetBizDB -E -I -i db\StreetBiz_Upgrade_Slot_Bearing.sql
   ============================================================ */
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.SidewalkSlots') IS NULL
    THROW 50001, N'SidewalkSlots does not exist: this is not a StreetBiz database. Nothing changed.', 1;
GO

IF COL_LENGTH(N'dbo.SidewalkSlots', N'bearing_degrees') IS NULL
BEGIN
    BEGIN TRAN;
    ALTER TABLE dbo.SidewalkSlots ADD bearing_degrees DECIMAL(5,2) NULL;
    COMMIT;
    PRINT N'SidewalkSlots.bearing_degrees added.';
END
ELSE
    PRINT N'SidewalkSlots.bearing_degrees already there.';
GO

-- A separate batch: the constraint can only name the column once the ALTER above has compiled.
IF OBJECT_ID(N'dbo.CK_SidewalkSlots_Bearing', N'C') IS NULL
BEGIN
    ALTER TABLE dbo.SidewalkSlots ADD CONSTRAINT CK_SidewalkSlots_Bearing
        CHECK (bearing_degrees IS NULL OR (bearing_degrees >= 0 AND bearing_degrees < 180));
    PRINT N'CK_SidewalkSlots_Bearing added.';
END
ELSE
    PRINT N'CK_SidewalkSlots_Bearing already there: nothing to do.';
GO
