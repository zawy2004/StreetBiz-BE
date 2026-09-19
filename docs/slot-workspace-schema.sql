/* ============================================================
   Slot workspace schema (vendor "Ô thuê" screen redesign)

   Adds the data the redesigned /vendor/slots screen needs:
     - slot detail: photo, power/water/trash amenities, business category
     - zone info: code, regulation reference, segment ends, application deadline
     - ward contact (name + phone) shown on the slot detail panel
     - zone fee components (fee table + quote)
     - street features (technical corridors, trees, hydrants ...)
     - slot holds (15-minute reservation, one row per slot)
     - RentalApplications.commitments_accepted_at

   Idempotent: every statement is guarded, so re-running is a no-op.
   Apply by hand (see docs/migration-guide.md -- the API never runs schema
   changes), then mirror it into db/StreetBiz_SQL_Server.sql and the dbml.
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ---- SidewalkSlots: detail columns ---- */
-- image_url is separate from proposal_photo_url on purpose: the latter is the
-- evidence of a vendor proposal and is bound by CK_SidewalkSlots_ProposalCoherent.
IF COL_LENGTH('SidewalkSlots', 'image_url') IS NULL
    ALTER TABLE SidewalkSlots ADD image_url NVARCHAR(500) NULL;
IF COL_LENGTH('SidewalkSlots', 'has_power') IS NULL
    ALTER TABLE SidewalkSlots ADD has_power BIT NOT NULL CONSTRAINT DF_SidewalkSlots_HasPower DEFAULT 0;
IF COL_LENGTH('SidewalkSlots', 'has_water') IS NULL
    ALTER TABLE SidewalkSlots ADD has_water BIT NOT NULL CONSTRAINT DF_SidewalkSlots_HasWater DEFAULT 0;
IF COL_LENGTH('SidewalkSlots', 'has_trash_bin') IS NULL
    ALTER TABLE SidewalkSlots ADD has_trash_bin BIT NOT NULL CONSTRAINT DF_SidewalkSlots_HasTrashBin DEFAULT 0;
IF COL_LENGTH('SidewalkSlots', 'business_category') IS NULL
    ALTER TABLE SidewalkSlots ADD business_category NVARCHAR(30) NULL;
GO

IF OBJECT_ID('CK_SidewalkSlots_BusinessCategory', 'C') IS NULL
    ALTER TABLE SidewalkSlots ADD CONSTRAINT CK_SidewalkSlots_BusinessCategory
        CHECK (business_category IS NULL
            OR business_category IN ('FOOD_BEVERAGE','RETAIL','SERVICES','CRAFTS','GENERAL'));
GO

/* ---- PricingZones: zone info ---- */
IF COL_LENGTH('PricingZones', 'zone_code') IS NULL
    ALTER TABLE PricingZones ADD zone_code NVARCHAR(30) NULL;
IF COL_LENGTH('PricingZones', 'regulation_ref') IS NULL
    ALTER TABLE PricingZones ADD regulation_ref NVARCHAR(120) NULL;
IF COL_LENGTH('PricingZones', 'segment_from') IS NULL
    ALTER TABLE PricingZones ADD segment_from NVARCHAR(150) NULL;
IF COL_LENGTH('PricingZones', 'segment_to') IS NULL
    ALTER TABLE PricingZones ADD segment_to NVARCHAR(150) NULL;
IF COL_LENGTH('PricingZones', 'application_deadline') IS NULL
    ALTER TABLE PricingZones ADD application_deadline DATE NULL;
GO

/* ---- AdministrativeUnits: ward contact ---- */
IF COL_LENGTH('AdministrativeUnits', 'contact_name') IS NULL
    ALTER TABLE AdministrativeUnits ADD contact_name NVARCHAR(150) NULL;
IF COL_LENGTH('AdministrativeUnits', 'contact_phone') IS NULL
    ALTER TABLE AdministrativeUnits ADD contact_phone NVARCHAR(20) NULL;
GO

/* ---- RentalApplications: stored commitments ---- */
IF COL_LENGTH('RentalApplications', 'commitments_accepted_at') IS NULL
    ALTER TABLE RentalApplications ADD commitments_accepted_at DATETIME2 NULL;
GO

/* ---- ZoneFeeComponents: fee table of a zone ---- */
-- PER_DAY components are multiplied by the term, PER_TERM ones are charged once.
IF OBJECT_ID('ZoneFeeComponents', 'U') IS NULL
CREATE TABLE ZoneFeeComponents (
    component_id    INT IDENTITY(1,1)  PRIMARY KEY,
    zone_id         INT                NOT NULL,
    component_name  NVARCHAR(150)      NOT NULL,
    calc_basis      NVARCHAR(10)       NOT NULL,
    unit_amount     DECIMAL(18,0)      NOT NULL,
    sort_order      INT                NOT NULL DEFAULT 0,
    CONSTRAINT CK_ZoneFeeComponents_Basis
        CHECK (calc_basis IN ('PER_DAY','PER_TERM')),
    CONSTRAINT CK_ZoneFeeComponents_Amount
        CHECK (unit_amount >= 0),
    CONSTRAINT FK_ZoneFeeComponents_Zone
        FOREIGN KEY (zone_id) REFERENCES PricingZones(zone_id)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ZoneFeeComponents_Zone')
    CREATE INDEX IX_ZoneFeeComponents_Zone ON ZoneFeeComponents(zone_id);
GO

/* ---- StreetFeatures: technical corridors and street furniture ---- */
-- blocks_business = 1 marks a corridor where no slot can operate (e.g. a
-- transformer station); the others are informational landmarks.
IF OBJECT_ID('StreetFeatures', 'U') IS NULL
CREATE TABLE StreetFeatures (
    feature_id       INT IDENTITY(1,1)  PRIMARY KEY,
    zone_id          INT                NOT NULL,
    feature_type     NVARCHAR(30)       NOT NULL,
    label            NVARCHAR(150)      NOT NULL,
    latitude         DECIMAL(9,6)       NOT NULL,
    longitude        DECIMAL(9,6)       NOT NULL,
    blocks_business  BIT                NOT NULL DEFAULT 0,
    note             NVARCHAR(200)      NULL,
    CONSTRAINT CK_StreetFeatures_Type
        CHECK (feature_type IN ('TRANSFORMER','HYDRANT','TREE','LIGHT_POLE','BUS_STOP','PARKING')),
    CONSTRAINT FK_StreetFeatures_Zone
        FOREIGN KEY (zone_id) REFERENCES PricingZones(zone_id)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StreetFeatures_Zone')
    CREATE INDEX IX_StreetFeatures_Zone ON StreetFeatures(zone_id);
GO

/* ---- SlotHolds: 15-minute reservation of a slot ---- */
-- PRIMARY KEY (slot_id) allows at most one hold per slot, which is what makes
-- two simultaneous "hold" requests safe: the loser hits the key violation.
-- An expired row is treated as absent (expires_at <= now); nothing sweeps them.
IF OBJECT_ID('SlotHolds', 'U') IS NULL
CREATE TABLE SlotHolds (
    slot_id          BIGINT     NOT NULL PRIMARY KEY,
    registration_id  BIGINT     NOT NULL,
    held_at          DATETIME2  NOT NULL DEFAULT SYSUTCDATETIME(),
    expires_at       DATETIME2  NOT NULL,
    CONSTRAINT CK_SlotHolds_Window
        CHECK (expires_at > held_at),
    CONSTRAINT FK_SlotHolds_Slot
        FOREIGN KEY (slot_id) REFERENCES SidewalkSlots(slot_id),
    CONSTRAINT FK_SlotHolds_Registration
        FOREIGN KEY (registration_id) REFERENCES BusinessRegistrations(registration_id)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SlotHolds_Registration')
    CREATE INDEX IX_SlotHolds_Registration ON SlotHolds(registration_id);
GO
