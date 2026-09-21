/* ============================================================
   StreetBiz — SQL Server Database Schema (T-SQL)
   Rebuilt to match the Sidewalk-first pivot finalized in Report 3
   (9 actors, 115 use cases, Core = registration/rental/permit/fee/
   penalty compliance workflow; Phase 2 = gated prepaid pickup-only
   marketplace). Generated fresh — does not extend any prior schema.

   Conventions:
   - Engine: SQL Server (T-SQL). Confirmed per project ways-of-working.
   - Surrogate keys: INT IDENTITY for small reference/config tables,
     BIGINT IDENTITY for transactional tables.
   - Enums: no native ENUM type in SQL Server — modelled as
     NVARCHAR + CHECK constraint for readability and easy ALTER.
   - Money: VND has no circulating subunit, so monetary columns use
     DECIMAL(18,0) rather than DECIMAL(18,2).
   - Timestamps: DATETIME2, stored in UTC (SYSUTCDATETIME()); the
     Asia/Ho_Chi_Minh display conversion (UI-09) happens in the app layer.
   - Forward references (PaymentTransactions -> Orders) are added via
     ALTER TABLE at the end of the script to keep CREATE TABLE order
     dependency-clean top to bottom.
   - This script creates structural/simple constraints only, plus five
     triggers and two views for rules that no CHECK constraint can express
     because they span rows, tables or time:
       TR_RentalContracts_NoOverlap          — one slot, one tenant at a time
       TR_RentalContracts_NoCancelWithDebt   — SIDE-07 is not an exit from money owed
       TR_Penalties_RequireIdentifiedOffender — cannot bill an unknown person
       TR_Storefronts_Phase2Gate             — BR-48 marketplace entry gate
       TR_RefundTransactions_NotMoreThanPaid — never refund more than came in
       vw_PermitValidity                     — the only correct answer to
                                               "is this QR good right now",
                                               derived from permit AND contract
       vw_VendorRatings                      — single answer to "what is this
                                               vendor's rating", since Phase 1
                                               comments and Phase 2 reviews are
                                               two different populations
     The two write-skew triggers (NoOverlap, NotMoreThanPaid) and the debt
     check take UPDLOCK/HOLDLOCK: under READ_COMMITTED_SNAPSHOT, which Azure
     SQL Database enables by default, an unhinted read sees a stale snapshot
     and two concurrent transactions each conclude they are the only one.
     Other cross-table business rules that need multi-row or workflow-state
     checks (e.g. BR-11 geofence radius, BR-12 one active adjacent
     contract per registration, BR-24 no-automatic-priority conflict
     resolution, BR-41 AI outputs never auto-decide) are enforced in
     the application/service layer, not the database, and are noted
     inline where relevant.
   - Two kinds of "this column may only point at a certain kind of row" are
     enforced the same way, because SQL Server cannot express either in a
     CHECK: the referencing table declares a persisted computed column holding
     a constant, and references the target on a composite key.
       ward_unit_id  -> must be a WARD, never a province or a district
                        (UQ_AdministrativeUnits_IdType makes it legal)
       reviewed_by / recorded_by / created_by / waived_by / resolved_by
                     -> must be an account holding the role that use case
                        assigns: WARD_AUTHORITY for the sidewalk and penalty
                        workflow, PLATFORM_ADMIN for the marketplace catalogue
                        and moderation (UQ_UserAccounts_IdRole makes it legal).
                        PermitScanLogs.scanner_role is conditional: a
                        WARD_INSPECTION demands a ward officer, a PUBLIC_CHECK
                        (BUY-02) is open to anyone or to nobody.
     Columns legitimately open to more than one role - ReportExports
     (WARD-14/15 and ADM-06/07), AIAssistanceLogs, OrderStatusHistory - carry
     no such constraint, and RentalContracts.cancelled_by belongs to the
     contract's own vendor rather than to a role, so it is left to the service
     layer.
   ============================================================ */

/* ============================================================
   HOW THIS FILE IS MAINTAINED  -- read before changing the database

   The database is built from exactly TWO scripts, and nothing else:
     db/StreetBiz_SQL_Server.sql   this file: schema + reference data
                                   (roles, wards, violation catalogue) + EF history stamp
     db/StreetBiz_Demo_Seed.sql    demo accounts and a full demo scenario (dev only)
   scripts/setup-local-db.ps1 runs both on an empty database.

   To change the schema:
     1. Edit the CREATE TABLE / constraint / index IN THIS FILE. Do not write an
        ALTER script, a patch script or a "fix" script -- and do not add .sql files
        under docs/ or anywhere else. A change that lives in a side file is invisible
        to the next person who builds a database.
     2. Add one line to the change log below (newest first).
     3. If the demo data is affected, update db/StreetBiz_Demo_Seed.sql in the same
        change. New reference data (a role, a ward, a violation type) goes in
        section 10 of this file instead.
     4. Verify by rebuilding an empty database: scripts/setup-local-db.ps1 -Recreate.
     5. Say in the PR that teammates must rebuild, and mention any manual step.

   The API never runs schema changes (docs/database.md).

   CHANGE LOG (newest first)
     2026-09-21  BusinessRegistrations: Mau so 01 owner/business fields, food-safety
                 commitment, manual identity check (identity_verified_*), cached AI check
                 (ai_check_*). New tables BusinessRegistrationHouseholdMembers and
                 KycVerificationResults. RegistrationEvidence accepts
                 IDENTITY_DOCUMENT_BACK and PORTRAIT_SELFIE. UserAccounts.sanction_authority_title.
     2026-09-21  Orders.storefront_address_snapshot folded in (was post-schema-migrations.sql).
     2026-09-20  BusinessRegistrations.id_number and biometric_consent_at,
                 PenaltyFeeSchedules.legal_basis, Penalties.decision_number/signer_name/
                 signer_title. Violation catalogue extended with the five legal codes.
     2026-09-19  Slot workspace: SidewalkSlots detail columns, PricingZones/
                 AdministrativeUnits info, ZoneFeeComponents, StreetFeatures, SlotHolds,
                 RentalApplications.commitments_accepted_at.
   ============================================================ */

-- CREATE DATABASE StreetBizDB;
-- GO
-- USE StreetBizDB;
-- GO

-- Required by the filtered unique index on ShoppingCarts. SSMS sets this ON by
-- default, sqlcmd does not, so state it explicitly or the script fails there.
SET QUOTED_IDENTIFIER ON;
GO


/* ============================================================
   1. IDENTITY & ADMINISTRATIVE REFERENCE
   ============================================================ */

CREATE TABLE AdministrativeUnits (
    unit_id         INT IDENTITY(1,1)  PRIMARY KEY,
    unit_type       NVARCHAR(20)       NOT NULL,
    unit_name       NVARCHAR(200)      NOT NULL,
    parent_unit_id  INT                NULL,
    created_at      DATETIME2          NOT NULL DEFAULT SYSUTCDATETIME(),
    contact_name    NVARCHAR(150)      NULL,  -- ward contact shown on the slot detail panel
    contact_phone   NVARCHAR(20)       NULL,
    CONSTRAINT CK_AdministrativeUnits_Type
        CHECK (unit_type IN ('PROVINCE','DISTRICT','WARD')),
    -- redundant as a key, but required as the target of the composite
    -- (unit_id, unit_type) foreign keys that pin ward_unit_id to a WARD
    CONSTRAINT UQ_AdministrativeUnits_IdType
        UNIQUE (unit_id, unit_type),
    CONSTRAINT FK_AdministrativeUnits_Parent
        FOREIGN KEY (parent_unit_id) REFERENCES AdministrativeUnits(unit_id)
);

-- GUEST is deliberately absent: an unauthenticated visitor is an actor in the
-- use-case model but never a row here, and a role code no account can hold is
-- dead data. Guest capability is decided by the absence of a token, not by a
-- role lookup.
--
-- role_code is the primary key rather than a surrogate id. Every table that
-- records "a ward officer decided this" or "an administrator did this" carries
-- a composite foreign key ending in a constant role code, and a constant can
-- only be written against a value that is stable and known at DDL time.
CREATE TABLE Roles (
    role_code   NVARCHAR(30)   PRIMARY KEY,
    role_name   NVARCHAR(100)  NOT NULL,
    CONSTRAINT CK_Roles_Code
        CHECK (role_code IN ('CUSTOMER','VENDOR','WARD_AUTHORITY','PLATFORM_ADMIN'))
);

-- AUTH subsystem: unified identity for all roles (AUTH-01..09)
CREATE TABLE UserAccounts (
    user_id             BIGINT IDENTITY(1,1)  PRIMARY KEY,
    phone_number        NVARCHAR(15)          NOT NULL UNIQUE,
    password_hash       NVARCHAR(255)         NOT NULL,
    full_name           NVARCHAR(150)         NULL,
    role_code           NVARCHAR(30)          NOT NULL,
    ward_unit_id        INT                   NULL,  -- ward the user operates/lives in, or (Ward Authority) administers
    -- constant, persisted, and only here so the composite FK below can force
    -- ward_unit_id to reference a WARD row rather than a province or district
    ward_unit_type      AS CAST(N'WARD' AS NVARCHAR(20)) PERSISTED,
    account_status      NVARCHAR(20)          NOT NULL DEFAULT 'ACTIVE',
    phone_verified_at   DATETIME2             NULL,
    created_at          DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at          DATETIME2             NULL,
    -- WARD-13: fixed title printed under a sanction decision (e.g. N'Chủ tịch UBND Phường ...').
    -- Set out of band by a platform admin, never typed by the officer; NULL = this account
    -- cannot sign sanction decisions.
    sanction_authority_title NVARCHAR(100)   NULL,
    CONSTRAINT CK_UserAccounts_Status
        CHECK (account_status IN ('ACTIVE','SUSPENDED','DEACTIVATED')),
    -- redundant as a key (user_id is already unique), but required as the
    -- target of every (actor_id, role) composite foreign key in this schema
    CONSTRAINT UQ_UserAccounts_IdRole
        UNIQUE (user_id, role_code),
    CONSTRAINT FK_UserAccounts_Role
        FOREIGN KEY (role_code) REFERENCES Roles(role_code),
    -- NULL ward_unit_id skips the check entirely (SQL Server MATCH SIMPLE),
    -- which is what a platform admin with no ward needs
    CONSTRAINT FK_UserAccounts_Ward
        FOREIGN KEY (ward_unit_id, ward_unit_type)
        REFERENCES AdministrativeUnits(unit_id, unit_type)
);
-- NOTE: one role per account. A vendor therefore browses and orders with the
-- same account they sell from; nothing in this schema forbids it, because
-- Orders.customer_user_id has no role restriction. If the two must be kept
-- apart, that is an application-layer rule, not a schema one.

-- AUTH-08 / AUTH-09
CREATE TABLE UserSessions (
    session_id          BIGINT IDENTITY(1,1)  PRIMARY KEY,
    user_id             BIGINT                NOT NULL,
    -- SHA-256 of the refresh token. The raw token is returned to the client
    -- once and never stored, so a leaked table cannot be replayed. This column
    -- is what makes AUTH-09 (revoke a specific device) actually enforceable:
    -- without it, revoked_at has nothing to match an incoming request against.
    refresh_token_hash  VARBINARY(32)         NOT NULL,
    device_info         NVARCHAR(255)         NULL,
    ip_address          NVARCHAR(45)          NULL,
    expires_at          DATETIME2             NOT NULL,
    created_at          DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    last_active_at      DATETIME2             NULL,
    revoked_at          DATETIME2             NULL,
    CONSTRAINT UQ_UserSessions_TokenHash
        UNIQUE (refresh_token_hash),
    CONSTRAINT FK_UserSessions_User
        FOREIGN KEY (user_id) REFERENCES UserAccounts(user_id)
);
CREATE INDEX IX_UserSessions_User ON UserSessions(user_id);

-- AUTH-01/02: phone verification. Keyed by phone_number rather than user_id
-- because a registration OTP is issued before any UserAccounts row exists.
-- The code itself is hashed for the same reason password_hash is: a dump of
-- this table must not let anyone complete someone else's verification.
CREATE TABLE OtpChallenges (
    challenge_id    BIGINT IDENTITY(1,1)  PRIMARY KEY,
    phone_number    NVARCHAR(15)          NOT NULL,
    purpose         NVARCHAR(20)          NOT NULL,
    code_hash       VARBINARY(32)         NOT NULL,  -- SHA-256 of the OTP
    attempt_count   TINYINT               NOT NULL DEFAULT 0,
    max_attempts    TINYINT               NOT NULL DEFAULT 5,
    expires_at      DATETIME2             NOT NULL,
    consumed_at     DATETIME2             NULL,
    created_at      DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_OtpChallenges_Purpose
        CHECK (purpose IN ('REGISTRATION','LOGIN','PASSWORD_RESET','PHONE_CHANGE')),
    CONSTRAINT CK_OtpChallenges_Attempts
        CHECK (attempt_count <= max_attempts)
);
CREATE INDEX IX_OtpChallenges_Lookup ON OtpChallenges(phone_number, purpose, expires_at);

-- Push notification targets. Notifications holds the in-app record; this holds
-- the device to actually deliver it to. Without it a mobile client can be
-- notified only while it is open.
CREATE TABLE UserDevices (
    user_device_id      BIGINT IDENTITY(1,1)  PRIMARY KEY,
    user_id             BIGINT                NOT NULL,
    device_identifier   NVARCHAR(180)         NOT NULL,  -- stable per install
    platform            NVARCHAR(20)          NOT NULL,
    push_token          NVARCHAR(500)         NULL,      -- FCM registration token
    is_active           BIT                   NOT NULL DEFAULT 1,
    last_seen_at        DATETIME2             NULL,
    created_at          DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_UserDevices_Platform
        CHECK (platform IN ('ANDROID','IOS','WEB')),
    CONSTRAINT UQ_UserDevices_Device
        UNIQUE (user_id, device_identifier),
    CONSTRAINT FK_UserDevices_User
        FOREIGN KEY (user_id) REFERENCES UserAccounts(user_id)
);
-- FCM reassigns a token to whichever install currently owns it, so the same
-- token must never sit on two rows or one push is delivered twice.
CREATE UNIQUE INDEX UQ_UserDevices_PushToken
    ON UserDevices(push_token)
    WHERE push_token IS NOT NULL;
CREATE INDEX IX_UserDevices_ActiveByUser
    ON UserDevices(user_id)
    WHERE is_active = 1;


/* ============================================================
   2. VENDOR BUSINESS REGISTRATION (REG)
   ============================================================ */

CREATE TABLE Vendors (
    vendor_id   BIGINT IDENTITY(1,1)  PRIMARY KEY,
    user_id     BIGINT                NOT NULL UNIQUE,
    created_at  DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_Vendors_User
        FOREIGN KEY (user_id) REFERENCES UserAccounts(user_id)
);

-- REG-01..05, WARD-04..06. vendor_type declared per registration (BR-05),
-- not per account, since one vendor may hold several registrations (BR-06).
CREATE TABLE BusinessRegistrations (
    registration_id           BIGINT IDENTITY(1,1)  PRIMARY KEY,
    vendor_id                 BIGINT                NOT NULL,
    vendor_type               NVARCHAR(20)          NOT NULL,
    display_name              NVARCHAR(180)         NOT NULL,
    declared_address          NVARCHAR(500)         NULL,
    address_latitude          DECIMAL(9,6)          NULL,
    address_longitude         DECIMAL(9,6)          NULL,
    ward_unit_id              INT                   NOT NULL,
    ward_unit_type            AS CAST(N'WARD' AS NVARCHAR(20)) PERSISTED,
    registration_status       NVARCHAR(30)          NOT NULL DEFAULT 'SUBMITTED',
    fast_track_flag           BIT                   NOT NULL DEFAULT 0,  -- REG-06
    reviewed_by               BIGINT                NULL,
    reviewer_role         AS CAST(N'WARD_AUTHORITY' AS NVARCHAR(30)) PERSISTED,
    review_decision_reason    NVARCHAR(500)         NULL,
    reviewed_at               DATETIME2             NULL,
    created_at                DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at                DATETIME2             NULL,
    -- Owner identity (Mau so 01, Phu luc II, Thong tu 68/2025/TT-BTC). id_number is the
    -- real CCCD number read by AI-OCR (AIC-01) -- never the phone number.
    id_number                 NVARCHAR(12)          NULL,
    id_type                   NVARCHAR(20)          NULL,
    id_issued_date            DATE                  NULL,
    id_issued_place           NVARCHAR(150)         NULL,
    owner_date_of_birth       DATE                  NULL,
    owner_gender              NVARCHAR(10)          NULL,
    owner_ethnicity           NVARCHAR(50)          NULL,
    owner_nationality         NVARCHAR(50)          NULL
        CONSTRAINT DF_BusinessRegistrations_owner_nationality DEFAULT (N'Việt Nam'),
    permanent_address         NVARCHAR(300)         NULL,
    contact_address           NVARCHAR(300)         NULL,
    -- Business line and scale.
    business_line             NVARCHAR(300)         NULL,
    business_line_code        NVARCHAR(20)          NULL,
    capital_amount            DECIMAL(18,0)         NULL,
    labor_count               INT                   NULL,
    planned_start_date        DATE                  NULL,
    -- Separate, explicit consents / declarations (Nghi dinh 356/2025/ND-CP: biometric
    -- consent must be its own unbundled action; food-safety is a self-declared commitment,
    -- not a certificate upload).
    biometric_consent_at      DATETIME2             NULL,
    food_safety_commitment_at DATETIME2             NULL,
    -- KYC gate: a ward officer's manual confirmation against the physical/chip CCCD.
    -- AI-OCR is decision support only (BR-41) and never substitutes for this; an APPROVE
    -- decision requires identity_verified_at (enforced in the application layer).
    identity_verified_by      BIGINT                NULL,
    identity_verified_at      DATETIME2             NULL,
    identity_verification_note NVARCHAR(500)        NULL,
    -- Last AI document-check result, cached so a detail page does not call the provider again.
    ai_check_result           NVARCHAR(MAX)         NULL,
    ai_checked_at             DATETIME2             NULL,
    CONSTRAINT CK_BusinessRegistrations_VendorType
        CHECK (vendor_type IN ('FIXED_STOREFRONT','ITINERANT')),
    CONSTRAINT CK_BusinessRegistrations_Status
        CHECK (registration_status IN
            ('DRAFT','SUBMITTED','UNDER_REVIEW','MORE_INFORMATION_REQUIRED','APPROVED','REJECTED','WITHDRAWN')),
    -- BR-07: Fixed Storefront requires a declared address; Itinerant does not.
    CONSTRAINT CK_BusinessRegistrations_FixedNeedsAddress
        CHECK (vendor_type = 'ITINERANT' OR declared_address IS NOT NULL),
    CONSTRAINT FK_BusinessRegistrations_Vendor
        FOREIGN KEY (vendor_id) REFERENCES Vendors(vendor_id),
    CONSTRAINT FK_BusinessRegistrations_Ward
        FOREIGN KEY (ward_unit_id, ward_unit_type)
        REFERENCES AdministrativeUnits(unit_id, unit_type),
    CONSTRAINT FK_BusinessRegistrations_Reviewer
        FOREIGN KEY (reviewed_by, reviewer_role)
        REFERENCES UserAccounts(user_id, role_code),
    CONSTRAINT FK_BusinessRegistrations_IdentityVerifiedBy
        FOREIGN KEY (identity_verified_by) REFERENCES UserAccounts(user_id)
);
CREATE INDEX IX_BusinessRegistrations_Vendor ON BusinessRegistrations(vendor_id);
CREATE INDEX IX_BusinessRegistrations_Status ON BusinessRegistrations(registration_status);
-- BR-09: at most one active (SUBMITTED/UNDER_REVIEW) registration per vendor,
-- enforced in the application layer (status transitions span multiple rows/time).

-- REG-02
CREATE TABLE RegistrationEvidence (
    evidence_id             BIGINT IDENTITY(1,1)  PRIMARY KEY,
    registration_id         BIGINT                NOT NULL,
    evidence_type           NVARCHAR(30)          NOT NULL,
    file_url                NVARCHAR(500)         NOT NULL,
    ocr_extracted_data      NVARCHAR(MAX)         NULL,  -- AIC-01 output (JSON)
    uploaded_at              DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    retention_expires_at    DATETIME2             NULL,  -- PRI-06 retention policy
    CONSTRAINT CK_RegistrationEvidence_Type
        -- IDENTITY_DOCUMENT is the CCCD front (kept so older rows stay valid); the back
        -- and a portrait selfie are captured for two-sided OCR and face match.
        CHECK (evidence_type IN ('IDENTITY_DOCUMENT','IDENTITY_DOCUMENT_BACK','PORTRAIT_SELFIE',
                                 'BUSINESS_LICENSE','ADDRESS_PROOF','OTHER')),
    CONSTRAINT FK_RegistrationEvidence_Registration
        FOREIGN KEY (registration_id) REFERENCES BusinessRegistrations(registration_id)
);
CREATE INDEX IX_RegistrationEvidence_Registration ON RegistrationEvidence(registration_id);

-- Mau so 01, "Thanh vien ho gia dinh cung gop von": 0..N members per registration.
CREATE TABLE BusinessRegistrationHouseholdMembers (
    member_id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    registration_id        BIGINT        NOT NULL
        CONSTRAINT FK_HouseholdMembers_Registration
        REFERENCES BusinessRegistrations(registration_id) ON DELETE CASCADE,
    full_name              NVARCHAR(150) NOT NULL,
    date_of_birth          DATE          NULL,
    id_number              NVARCHAR(12)  NULL,
    relationship_to_owner  NVARCHAR(50)  NULL,
    capital_contribution   DECIMAL(18,0) NULL,
    created_at             DATETIME2     NOT NULL
        CONSTRAINT DF_HouseholdMembers_CreatedAt DEFAULT (SYSUTCDATETIME())
);

-- REG-02 eKYC: server-recorded outcome of each AI check (CCCD OCR, face match). The score
-- is written where it is computed and read back by the ward officer's screen -- never
-- round-tripped through the client. Decision support only (BR-41): these rows never
-- approve, reject or change any state. registration_id is NULL until the wizard creates
-- the registration (the checks run earlier), then backfilled.
CREATE TABLE KycVerificationResults (
    kyc_result_id        BIGINT IDENTITY(1,1) PRIMARY KEY,
    user_id              BIGINT         NOT NULL
        CONSTRAINT FK_KycVerificationResults_User REFERENCES UserAccounts(user_id),
    registration_id      BIGINT         NULL
        CONSTRAINT FK_KycVerificationResults_Registration REFERENCES BusinessRegistrations(registration_id),
    check_type           NVARCHAR(30)   NOT NULL,   -- 'ID_CARD_OCR' | 'FACE_MATCH'
    provider             NVARCHAR(30)   NOT NULL,   -- 'FPT.AI'
    is_match             BIT            NULL,       -- FACE_MATCH only
    similarity_percent   DECIMAL(5,2)   NULL,       -- FACE_MATCH only
    confidence_percent   INT            NULL,       -- ID_CARD_OCR only
    extracted_id_number  NVARCHAR(12)   NULL,
    warnings             NVARCHAR(1000) NULL,
    created_at           DATETIME2      NOT NULL
        CONSTRAINT DF_KycVerificationResults_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_KycVerificationResults_CheckType
        CHECK (check_type IN ('ID_CARD_OCR', 'FACE_MATCH'))
);
CREATE INDEX IX_KycVerificationResults_User_Created
    ON KycVerificationResults (user_id, created_at DESC);
CREATE INDEX IX_KycVerificationResults_Registration
    ON KycVerificationResults (registration_id) WHERE registration_id IS NOT NULL;


/* ============================================================
   3. SIDEWALK SLOT & RENTAL (SIDE, WARD-01/02)
   ============================================================ */

-- WARD-02: pricing/time-window configuration per zone within a ward
CREATE TABLE PricingZones (
    zone_id          INT IDENTITY(1,1)  PRIMARY KEY,
    ward_unit_id     INT                NOT NULL,
    ward_unit_type   AS CAST(N'WARD' AS NVARCHAR(20)) PERSISTED,
    zone_name        NVARCHAR(150)      NOT NULL,
    price_per_day    DECIMAL(18,0)      NOT NULL,
    available_from   TIME               NULL,
    available_to     TIME               NULL,
    created_by       BIGINT             NOT NULL,
    creator_role          AS CAST(N'WARD_AUTHORITY' AS NVARCHAR(30)) PERSISTED,
    created_at       DATETIME2          NOT NULL DEFAULT SYSUTCDATETIME(),
    zone_code            NVARCHAR(30)   NULL,
    regulation_ref       NVARCHAR(120)  NULL,  -- decision the zone is opened under
    segment_from         NVARCHAR(150)  NULL,  -- street segment ends
    segment_to           NVARCHAR(150)  NULL,
    application_deadline DATE           NULL,
    CONSTRAINT UQ_PricingZones_NamePerWard
        UNIQUE (ward_unit_id, zone_name),
    CONSTRAINT FK_PricingZones_Ward
        FOREIGN KEY (ward_unit_id, ward_unit_type)
        REFERENCES AdministrativeUnits(unit_id, unit_type),
    CONSTRAINT FK_PricingZones_CreatedBy
        FOREIGN KEY (created_by, creator_role)
        REFERENCES UserAccounts(user_id, role_code)
);

-- WARD-01, SIDE-01/02/11, WARD-16
-- A slot has no ward_unit_id of its own: its ward is PricingZones.ward_unit_id,
-- reached through zone_id. Storing it twice only creates a way for the two to
-- disagree, and "slots in ward X" is one join away.
CREATE TABLE SidewalkSlots (
    slot_id                       BIGINT IDENTITY(1,1)  PRIMARY KEY,
    slot_code                     NVARCHAR(30)          NOT NULL UNIQUE,
    zone_id                       INT                   NOT NULL,
    latitude                      DECIMAL(9,6)          NOT NULL,
    longitude                     DECIMAL(9,6)          NOT NULL,
    width_meters                  DECIMAL(5,2)          NULL,
    length_meters                 DECIMAL(5,2)          NULL,
    slot_status                   NVARCHAR(20)          NOT NULL DEFAULT 'AVAILABLE',
    source                        NVARCHAR(20)          NOT NULL DEFAULT 'WARD_DEFINED',
    proposed_by_registration_id   BIGINT                NULL,  -- SIDE-11
    proposal_review_status        NVARCHAR(20)          NULL,  -- WARD-16
    proposal_photo_url            NVARCHAR(500)         NULL,  -- WARD-16 evidence
    proposal_reviewed_by          BIGINT                NULL,
    proposal_reviewer_role AS CAST(N'WARD_AUTHORITY' AS NVARCHAR(30)) PERSISTED,
    proposal_review_reason        NVARCHAR(500)         NULL,
    created_at                    DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    -- Slot detail (vendor slot workspace). image_url is not proposal_photo_url:
    -- that one is proposal evidence and is bound by CK_SidewalkSlots_ProposalCoherent.
    image_url                     NVARCHAR(500)         NULL,
    has_power                     BIT                   NOT NULL DEFAULT 0,
    has_water                     BIT                   NOT NULL DEFAULT 0,
    has_trash_bin                 BIT                   NOT NULL DEFAULT 0,
    business_category             NVARCHAR(30)          NULL,  -- advisory, never enforced on applications
    CONSTRAINT CK_SidewalkSlots_Status
        CHECK (slot_status IN ('AVAILABLE','PENDING_APPLICATION','ACTIVE','SUSPENDED')),
    CONSTRAINT CK_SidewalkSlots_Source
        CHECK (source IN ('WARD_DEFINED','VENDOR_PROPOSED')),
    CONSTRAINT CK_SidewalkSlots_BusinessCategory
        CHECK (business_category IS NULL
            OR business_category IN ('FOOD_BEVERAGE','RETAIL','SERVICES','CRAFTS','GENERAL')),
    CONSTRAINT CK_SidewalkSlots_ProposalStatus
        CHECK (proposal_review_status IS NULL OR proposal_review_status IN ('PENDING','APPROVED','REJECTED')),
    -- SIDE-11 / WARD-16: the proposal_* columns describe a vendor proposal and
    -- are meaningless on a ward-defined slot. Keep the two shapes from mixing.
    CONSTRAINT CK_SidewalkSlots_ProposalCoherent
        CHECK (
            (source = 'WARD_DEFINED'
                 AND proposed_by_registration_id IS NULL
                 AND proposal_review_status      IS NULL
                 AND proposal_photo_url          IS NULL)
         OR (source = 'VENDOR_PROPOSED'
                 AND proposed_by_registration_id IS NOT NULL
                 AND proposal_review_status      IS NOT NULL
                 -- WARD-16 asks an officer to judge a location that is not on
                 -- the ward's own grid. Without a picture there is nothing to
                 -- judge, so a proposal carries its evidence or is not a proposal.
                 AND proposal_photo_url          IS NOT NULL)
        ),
    CONSTRAINT FK_SidewalkSlots_Zone
        FOREIGN KEY (zone_id) REFERENCES PricingZones(zone_id),
    CONSTRAINT FK_SidewalkSlots_ProposedBy
        FOREIGN KEY (proposed_by_registration_id) REFERENCES BusinessRegistrations(registration_id),
    CONSTRAINT FK_SidewalkSlots_ProposalReviewer
        FOREIGN KEY (proposal_reviewed_by, proposal_reviewer_role)
        REFERENCES UserAccounts(user_id, role_code)
);
CREATE INDEX IX_SidewalkSlots_Status ON SidewalkSlots(slot_status);
CREATE INDEX IX_SidewalkSlots_Zone   ON SidewalkSlots(zone_id);

-- Fee table of a zone. PER_DAY components are multiplied by the term, PER_TERM
-- ones are charged once; the vendor quote is informational (WARD-08 issues the
-- real fee schedule).
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
CREATE INDEX IX_ZoneFeeComponents_Zone ON ZoneFeeComponents(zone_id);

-- Technical corridors and street furniture along a zone. blocks_business = 1
-- marks a corridor where no slot can operate (e.g. a transformer station).
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
CREATE INDEX IX_StreetFeatures_Zone ON StreetFeatures(zone_id);

-- Short reservation of a slot while a vendor fills in an application.
-- PRIMARY KEY (slot_id) = at most one hold per slot, which is what makes two
-- simultaneous hold requests safe (the loser hits the key violation). An
-- expired row is treated as absent (expires_at <= now); nothing sweeps them.
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
CREATE INDEX IX_SlotHolds_Registration ON SlotHolds(registration_id);

-- SIDE-03A/03B, WARD-07/08
CREATE TABLE RentalApplications (
    application_id           BIGINT IDENTITY(1,1)  PRIMARY KEY,
    registration_id          BIGINT                NOT NULL,
    slot_id                  BIGINT                NOT NULL,
    application_method       NVARCHAR(20)          NOT NULL,
    requested_term_days      INT                   NOT NULL,
    application_status       NVARCHAR(30)          NOT NULL DEFAULT 'PENDING',
    reviewed_by               BIGINT                NULL,
    reviewer_role         AS CAST(N'WARD_AUTHORITY' AS NVARCHAR(30)) PERSISTED,
    review_decision_reason   NVARCHAR(500)         NULL,
    reviewed_at               DATETIME2             NULL,
    created_at                DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    commitments_accepted_at   DATETIME2             NULL,  -- when the vendor ticked the commitments
    CONSTRAINT CK_RentalApplications_Method
        CHECK (application_method IN ('AUTO_ADJACENT','MANUAL_SELECTED')),
    CONSTRAINT CK_RentalApplications_Status
        CHECK (application_status IN
            ('PENDING','UNDER_REVIEW','MORE_INFORMATION_REQUIRED','APPROVED','REJECTED','WITHDRAWN')),
    CONSTRAINT CK_RentalApplications_TermPositive
        CHECK (requested_term_days > 0),
    CONSTRAINT FK_RentalApplications_Registration
        FOREIGN KEY (registration_id) REFERENCES BusinessRegistrations(registration_id),
    CONSTRAINT FK_RentalApplications_Slot
        FOREIGN KEY (slot_id) REFERENCES SidewalkSlots(slot_id),
    CONSTRAINT FK_RentalApplications_Reviewer
        FOREIGN KEY (reviewed_by, reviewer_role)
        REFERENCES UserAccounts(user_id, role_code)
);
CREATE INDEX IX_RentalApplications_Slot ON RentalApplications(slot_id);
CREATE INDEX IX_RentalApplications_Registration ON RentalApplications(registration_id);
-- BR-16: approval requires registration_status = APPROVED — enforced in application layer.
-- BR-11 (geofence radius) and BR-12 (one active adjacent contract per registration)
-- are enforced in the application layer at approval time.

-- WARD-08 approval outcome
CREATE TABLE RentalContracts (
    contract_id           BIGINT IDENTITY(1,1)  PRIMARY KEY,
    application_id        BIGINT                NOT NULL UNIQUE,
    slot_id               BIGINT                NOT NULL,
    vendor_id             BIGINT                NOT NULL,  -- current owner; changes on approved transfer
    start_date            DATE                  NOT NULL,
    end_date              DATE                  NOT NULL,
    contract_status       NVARCHAR(20)          NOT NULL DEFAULT 'ACTIVE',
    -- SIDE-07. Returning a slot has no ward actor in the use-case list, so it
    -- is the vendor's own action and needs no approval row - but a public
    -- authority still has to be able to say who gave the slot back and why.
    cancelled_by          BIGINT                NULL,
    cancellation_reason   NVARCHAR(500)         NULL,
    cancelled_at          DATETIME2             NULL,
    created_at            DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at            DATETIME2             NULL,
    CONSTRAINT CK_RentalContracts_Status
        CHECK (contract_status IN ('ACTIVE','EXPIRED','CANCELLED','SUSPENDED','REVOKED')),
    CONSTRAINT CK_RentalContracts_DateOrder
        CHECK (end_date > start_date),
    CONSTRAINT CK_RentalContracts_CancellationEvidence
        CHECK (contract_status <> 'CANCELLED'
               OR (cancelled_by IS NOT NULL AND cancelled_at IS NOT NULL)),
    CONSTRAINT FK_RentalContracts_Application
        FOREIGN KEY (application_id) REFERENCES RentalApplications(application_id),
    CONSTRAINT FK_RentalContracts_Slot
        FOREIGN KEY (slot_id) REFERENCES SidewalkSlots(slot_id),
    CONSTRAINT FK_RentalContracts_Vendor
        FOREIGN KEY (vendor_id) REFERENCES Vendors(vendor_id),
    CONSTRAINT FK_RentalContracts_CancelledBy
        FOREIGN KEY (cancelled_by) REFERENCES UserAccounts(user_id)
);
CREATE INDEX IX_RentalContracts_Vendor ON RentalContracts(vendor_id);
CREATE INDEX IX_RentalContracts_Status ON RentalContracts(contract_status);
GO
-- A sidewalk slot is a physical place: it cannot be let to two vendors at once.
-- slot_status alone cannot express this (it is a single flag with no date range,
-- so it does not stop two future contracts being approved for the same slot).
-- SUSPENDED counts as occupied — the vendor still holds the slot, they are just
-- barred from trading. EXPIRED / CANCELLED / REVOKED free it.
CREATE TRIGGER TR_RentalContracts_NoOverlap
ON RentalContracts
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1
        FROM inserted i
        -- UPDLOCK/HOLDLOCK, not decoration: under READ_COMMITTED_SNAPSHOT (the
        -- default on Azure SQL Database) an unhinted read sees a pre-transaction
        -- snapshot, so two concurrent approvals each find the slot free and both
        -- commit. Verified: without these hints the slot double-books.
        JOIN RentalContracts c WITH (UPDLOCK, HOLDLOCK)
          ON c.slot_id      = i.slot_id
         AND c.contract_id <> i.contract_id
         AND c.contract_status IN ('ACTIVE','SUSPENDED')
         AND i.start_date <= c.end_date      -- half-open overlap test
         AND c.start_date <= i.end_date
        WHERE i.contract_status IN ('ACTIVE','SUSPENDED')
    )
    BEGIN
        RAISERROR (N'Slot already has an active contract overlapping this period.', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END
END;
GO

-- SIDE-07. Returning a slot is the vendor's own act with no ward approval in
-- the use-case list, which is fine for the slot itself but must not become an
-- exit from money already owed. BR-27 already refuses a slot TRANSFER while a
-- balance is outstanding; without the same rule here the vendor simply hands
-- the slot back instead and the debt is stranded on a closed contract.
--
-- Only a voluntary CANCELLED is blocked. EXPIRED is the calendar running out
-- and cannot be refused, and REVOKED is the ward acting against the vendor -
-- neither should be held hostage by an unpaid bill. Instalments not yet due do
-- not block either: giving the slot back should not bill you for months you
-- will not use it.
CREATE TRIGGER TR_RentalContracts_NoCancelWithDebt
ON RentalContracts
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT UPDATE(contract_status) RETURN;

    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN deleted  d ON d.contract_id = i.contract_id
        WHERE i.contract_status = 'CANCELLED'
          AND d.contract_status <> 'CANCELLED'
          AND (
                EXISTS (
                    SELECT 1
                    FROM FeeSchedules fs
                    -- locked for the same reason as TR_RentalContracts_NoOverlap:
                    -- under RCSI an unhinted read would miss a fee that another
                    -- transaction is marking OVERDUE right now
                    JOIN FeeScheduleItems fi WITH (UPDLOCK, HOLDLOCK)
                      ON fi.fee_schedule_id = fs.fee_schedule_id
                    WHERE fs.contract_id = i.contract_id
                      AND fi.item_status = 'OVERDUE'
                )
             OR EXISTS (
                    SELECT 1
                    FROM Violations v
                    JOIN Penalties p WITH (UPDLOCK, HOLDLOCK)
                      ON p.violation_id = v.violation_id
                    WHERE v.contract_id = i.contract_id
                      AND p.penalty_status = 'UNPAID'
                )
          )
    )
    BEGIN
        RAISERROR (N'Cannot return a slot while fees are overdue or penalties unpaid.', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END
END;
GO

-- SIDE-08, BR-19/20
-- contract_id is not UNIQUE: revoking a permit and issuing a replacement for
-- the same contract has to be possible, and the revoked row is the audit trail.
-- The filtered index below is what keeps only one live permit per contract.
CREATE TABLE DigitalPermits (
    permit_id             BIGINT IDENTITY(1,1)  PRIMARY KEY,
    contract_id           BIGINT                NOT NULL,
    qr_payload            NVARCHAR(500)         NOT NULL UNIQUE,  -- signed token, no PII
    permit_status         NVARCHAR(20)          NOT NULL DEFAULT 'ACTIVE',
    issued_at             DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    suspended_at          DATETIME2             NULL,
    suspension_reason     NVARCHAR(500)         NULL,
    revoked_at            DATETIME2             NULL,
    revocation_reason     NVARCHAR(500)         NULL,
    CONSTRAINT CK_DigitalPermits_Status
        CHECK (permit_status IN ('ACTIVE','SUSPENDED','REVOKED')),
    CONSTRAINT CK_DigitalPermits_StatusCoherent
        CHECK (
            (permit_status = 'ACTIVE'    AND revoked_at IS NULL)
         OR (permit_status = 'SUSPENDED' AND suspended_at IS NOT NULL AND revoked_at IS NULL)
         OR (permit_status = 'REVOKED'   AND revoked_at IS NOT NULL)
        ),
    CONSTRAINT FK_DigitalPermits_Contract
        FOREIGN KEY (contract_id) REFERENCES RentalContracts(contract_id)
);
-- a suspended permit is still that contract's permit; only revocation frees
-- the contract for a replacement
CREATE UNIQUE INDEX UQ_DigitalPermits_LivePerContract
    ON DigitalPermits(contract_id)
    WHERE permit_status <> 'REVOKED';

-- SIDE-12/13, WARD-18
CREATE TABLE SlotTransferRequests (
    transfer_id              BIGINT IDENTITY(1,1)  PRIMARY KEY,
    contract_id              BIGINT                NOT NULL,
    from_vendor_id           BIGINT                NOT NULL,
    to_vendor_id             BIGINT                NOT NULL,
    transfer_status          NVARCHAR(20)          NOT NULL DEFAULT 'PENDING',
    initiated_at              DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    accepted_at                DATETIME2             NULL,
    reviewed_by                BIGINT                NULL,
    reviewer_role         AS CAST(N'WARD_AUTHORITY' AS NVARCHAR(30)) PERSISTED,
    review_decision_reason    NVARCHAR(500)         NULL,
    reviewed_at                DATETIME2             NULL,
    CONSTRAINT CK_SlotTransferRequests_Status
        CHECK (transfer_status IN ('PENDING','ACCEPTED_BY_RECEIVER','APPROVED','REJECTED')),
    CONSTRAINT CK_SlotTransferRequests_DifferentVendors
        CHECK (from_vendor_id <> to_vendor_id),
    CONSTRAINT FK_SlotTransferRequests_Contract
        FOREIGN KEY (contract_id) REFERENCES RentalContracts(contract_id),
    CONSTRAINT FK_SlotTransferRequests_FromVendor
        FOREIGN KEY (from_vendor_id) REFERENCES Vendors(vendor_id),
    CONSTRAINT FK_SlotTransferRequests_ToVendor
        FOREIGN KEY (to_vendor_id) REFERENCES Vendors(vendor_id),
    CONSTRAINT FK_SlotTransferRequests_Reviewer
        FOREIGN KEY (reviewed_by, reviewer_role)
        REFERENCES UserAccounts(user_id, role_code)
);
-- BR-26 (receiver must hold an approved registration) and BR-27 (no outstanding
-- balance) are validated in the application layer before WARD-18 approval.

-- WARD-11 and BUY-02. Both use cases scan the same QR, so both land here: a
-- ward officer inspecting on site, and a member of the public checking whether
-- a stall is legitimate. Without this table an enforcement system cannot show
-- that an inspection ever happened, which is the point of WARD-11.
-- scanned_by is NULL for a guest scan (BUY-02 lists Guest as an actor), and
-- qr_payload is stored raw so a scan of an unknown or forged code is still a
-- record rather than a lost event.
CREATE TABLE PermitScanLogs (
    scan_id        BIGINT IDENTITY(1,1)  PRIMARY KEY,
    permit_id      BIGINT                NULL,  -- NULL when the payload matched nothing
    qr_payload     NVARCHAR(500)         NOT NULL,
    scanned_by     BIGINT                NULL,  -- NULL = unauthenticated guest
    scan_context   NVARCHAR(20)          NOT NULL,
    -- A ward inspection must be carried out by an actual ward officer. A public
    -- check (BUY-02) is anyone or nobody, so the constant collapses to NULL and
    -- SQL Server skips the composite foreign key entirely for those rows.
    scanner_role   AS CASE WHEN scan_context = 'WARD_INSPECTION'
                          THEN CAST(N'WARD_AUTHORITY' AS NVARCHAR(30)) END PERSISTED,
    scan_result    NVARCHAR(20)          NOT NULL,
    -- optional on purpose: most scans are a routine identity check with nothing
    -- to photograph. The officer shoots when there is something to record, and
    -- that picture is what turns a scan into evidence for WARD-12.
    photo_url      NVARCHAR(500)         NULL,
    latitude       DECIMAL(9,6)          NULL,
    longitude      DECIMAL(9,6)          NULL,
    scanned_at     DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_PermitScanLogs_Context
        CHECK (scan_context IN ('WARD_INSPECTION','PUBLIC_CHECK')),
    CONSTRAINT CK_PermitScanLogs_Result
        CHECK (scan_result IN ('VALID','NOT_YET_VALID','SUSPENDED','REVOKED','EXPIRED','NOT_FOUND')),
    CONSTRAINT CK_PermitScanLogs_NotFoundHasNoPermit
        CHECK (scan_result <> 'NOT_FOUND' OR permit_id IS NULL),
    CONSTRAINT CK_PermitScanLogs_InspectionHasOfficer
        CHECK (scan_context <> 'WARD_INSPECTION' OR scanned_by IS NOT NULL),
    CONSTRAINT FK_PermitScanLogs_Permit
        FOREIGN KEY (permit_id) REFERENCES DigitalPermits(permit_id),
    CONSTRAINT FK_PermitScanLogs_ScannedBy
        FOREIGN KEY (scanned_by, scanner_role)
        REFERENCES UserAccounts(user_id, role_code)
);
CREATE INDEX IX_PermitScanLogs_Permit ON PermitScanLogs(permit_id, scanned_at);

-- SIDE-06 (vendor asks) and WARD-09 (ward approves). Renewal is not a new
-- RentalApplications row: the vendor already holds the slot, nothing is being
-- allocated, and WARD-09 is a separate decision from WARD-08. On approval the
-- contract's end_date moves out to new_end_date and its fee schedule is
-- superseded by the next FeeSchedules revision.
CREATE TABLE RenewalRequests (
    renewal_id               BIGINT IDENTITY(1,1)  PRIMARY KEY,
    contract_id              BIGINT                NOT NULL,
    requested_term_days      INT                   NOT NULL,
    renewal_status           NVARCHAR(30)          NOT NULL DEFAULT 'PENDING',
    new_end_date             DATE                  NULL,  -- set on approval
    reviewed_by              BIGINT                NULL,
    reviewer_role         AS CAST(N'WARD_AUTHORITY' AS NVARCHAR(30)) PERSISTED,
    review_decision_reason   NVARCHAR(500)         NULL,
    reviewed_at              DATETIME2             NULL,
    created_at               DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_RenewalRequests_Status
        CHECK (renewal_status IN ('PENDING','UNDER_REVIEW','APPROVED','REJECTED','WITHDRAWN')),
    CONSTRAINT CK_RenewalRequests_TermPositive
        CHECK (requested_term_days > 0),
    CONSTRAINT CK_RenewalRequests_ApprovalHasEndDate
        CHECK (renewal_status <> 'APPROVED' OR new_end_date IS NOT NULL),
    CONSTRAINT FK_RenewalRequests_Contract
        FOREIGN KEY (contract_id) REFERENCES RentalContracts(contract_id),
    CONSTRAINT FK_RenewalRequests_Reviewer
        FOREIGN KEY (reviewed_by, reviewer_role)
        REFERENCES UserAccounts(user_id, role_code)
);
-- a vendor cannot queue two renewals for the same contract
CREATE UNIQUE INDEX UQ_RenewalRequests_OpenPerContract
    ON RenewalRequests(contract_id)
    WHERE renewal_status IN ('PENDING','UNDER_REVIEW');

-- SIDE-09 (vendor moves), SIDE-10 (the adjacent slot has to go back) and
-- WARD-17 (the ward settles who gets it) are one workflow, not three edits to
-- BusinessRegistrations.declared_address. Only FIXED_STOREFRONT registrations
-- can raise one: an itinerant vendor has no declared address to change, and
-- no slot is tied to their address.
CREATE TABLE AddressChangeRequests (
    address_change_id          BIGINT IDENTITY(1,1)  PRIMARY KEY,
    registration_id            BIGINT                NOT NULL,
    new_address                NVARCHAR(500)         NOT NULL,
    new_latitude               DECIMAL(9,6)          NULL,
    new_longitude              DECIMAL(9,6)          NULL,
    released_contract_id       BIGINT                NULL,  -- SIDE-10: slot given up
    requested_new_slot_id      BIGINT                NULL,  -- slot adjacent to the new address
    change_status              NVARCHAR(30)          NOT NULL DEFAULT 'PENDING',
    conflict_resolution_note   NVARCHAR(500)         NULL,  -- WARD-17
    reviewed_by                BIGINT                NULL,
    reviewer_role         AS CAST(N'WARD_AUTHORITY' AS NVARCHAR(30)) PERSISTED,
    reviewed_at                DATETIME2             NULL,
    created_at                 DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_AddressChangeRequests_Status
        CHECK (change_status IN ('PENDING','UNDER_REVIEW','APPROVED','REJECTED','WITHDRAWN')),
    CONSTRAINT FK_AddressChangeRequests_Registration
        FOREIGN KEY (registration_id) REFERENCES BusinessRegistrations(registration_id),
    CONSTRAINT FK_AddressChangeRequests_ReleasedContract
        FOREIGN KEY (released_contract_id) REFERENCES RentalContracts(contract_id),
    CONSTRAINT FK_AddressChangeRequests_NewSlot
        FOREIGN KEY (requested_new_slot_id) REFERENCES SidewalkSlots(slot_id),
    CONSTRAINT FK_AddressChangeRequests_Reviewer
        FOREIGN KEY (reviewed_by, reviewer_role)
        REFERENCES UserAccounts(user_id, role_code)
);
CREATE UNIQUE INDEX UQ_AddressChangeRequests_OpenPerRegistration
    ON AddressChangeRequests(registration_id)
    WHERE change_status IN ('PENDING','UNDER_REVIEW');


/* ============================================================
   4. BUYER TRUST & VERIFICATION (BUY)
   ============================================================ */

-- BUY-04. This is the Phase 1 trust signal: anyone who has met the vendor on
-- the street can rate them, with no purchase involved. Reviews (REV-01) is the
-- Phase 2 signal and requires a completed order. They measure different
-- populations and neither replaces the other, so both keep their rating and
-- vw_VendorRatings exposes them as two separate numbers. The UI must show the
-- verified one as the headline; it must never average the two together.
CREATE TABLE VendorComments (
    comment_id          BIGINT IDENTITY(1,1)  PRIMARY KEY,
    vendor_id           BIGINT                NOT NULL,
    customer_user_id    BIGINT                NOT NULL,
    rating              SMALLINT              NULL,
    comment_text        NVARCHAR(1000)        NULL,
    created_at          DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_VendorComments_Rating
        CHECK (rating IS NULL OR rating BETWEEN 1 AND 5),
    -- one voice per person per vendor, otherwise the average is trivially gamed
    CONSTRAINT UQ_VendorComments_OnePerCustomer
        UNIQUE (vendor_id, customer_user_id),
    CONSTRAINT FK_VendorComments_Vendor
        FOREIGN KEY (vendor_id) REFERENCES Vendors(vendor_id),
    CONSTRAINT FK_VendorComments_Customer
        FOREIGN KEY (customer_user_id) REFERENCES UserAccounts(user_id)
);

-- BUY-05, AIB-01
CREATE TABLE VendorReports (
    report_id                BIGINT IDENTITY(1,1)  PRIMARY KEY,
    reporter_user_id         BIGINT                NOT NULL,
    vendor_id                BIGINT                NULL,
    slot_id                  BIGINT                NULL,
    scanned_permit_id        BIGINT                NULL,
    report_reason             NVARCHAR(500)         NOT NULL,
    evidence_url               NVARCHAR(500)         NULL,
    ai_extracted_location     NVARCHAR(200)         NULL,  -- AIB-01 output
    report_status              NVARCHAR(20)          NOT NULL DEFAULT 'PENDING',
    reviewed_by                 BIGINT                NULL,
    reviewer_role         AS CAST(N'WARD_AUTHORITY' AS NVARCHAR(30)) PERSISTED,
    reviewed_at                  DATETIME2             NULL,
    created_at                   DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_VendorReports_Status
        CHECK (report_status IN ('PENDING','REVIEWED','DISMISSED')),
    -- BR-40: a report must reference at least one of vendor / slot / scanned permit.
    CONSTRAINT CK_VendorReports_HasReference
        CHECK (vendor_id IS NOT NULL OR slot_id IS NOT NULL OR scanned_permit_id IS NOT NULL),
    CONSTRAINT FK_VendorReports_Reporter
        FOREIGN KEY (reporter_user_id) REFERENCES UserAccounts(user_id),
    CONSTRAINT FK_VendorReports_Vendor
        FOREIGN KEY (vendor_id) REFERENCES Vendors(vendor_id),
    CONSTRAINT FK_VendorReports_Slot
        FOREIGN KEY (slot_id) REFERENCES SidewalkSlots(slot_id),
    CONSTRAINT FK_VendorReports_Permit
        FOREIGN KEY (scanned_permit_id) REFERENCES DigitalPermits(permit_id),
    CONSTRAINT FK_VendorReports_Reviewer
        FOREIGN KEY (reviewed_by, reviewer_role)
        REFERENCES UserAccounts(user_id, role_code)
);


/* ============================================================
   5. FEE, PENALTY & INVOICING (FEE, WARD-03, SYS-03/04/05)
   ============================================================ */

-- The shared vocabulary for WARD-03 and WARD-12. Before this existed both
-- tables held a free NVARCHAR(50), so a violation could be recorded with a
-- type that no penalty schedule priced, and the officer would find no amount
-- to charge. A natural key keeps the code readable in both tables.
CREATE TABLE ViolationTypes (
    violation_type_code   NVARCHAR(50)   PRIMARY KEY,
    description           NVARCHAR(200)  NOT NULL,
    is_active             BIT            NOT NULL DEFAULT 1
);

-- WARD-03 config. Rates change; penalties already issued must keep the rate
-- they were issued at, so a rate is never edited in place — it is closed with
-- effective_to and a new row opened. The filtered index guarantees exactly one
-- open rate per ward and violation type, which is what BR-33 looks up.
CREATE TABLE PenaltyFeeSchedules (
    penalty_schedule_id   INT IDENTITY(1,1)  PRIMARY KEY,
    ward_unit_id          INT                NOT NULL,
    ward_unit_type        AS CAST(N'WARD' AS NVARCHAR(20)) PERSISTED,
    violation_type        NVARCHAR(50)       NOT NULL,
    penalty_amount        DECIMAL(18,0)      NOT NULL,
    effective_from        DATE               NOT NULL DEFAULT CAST(SYSUTCDATETIME() AS DATE),
    effective_to          DATE               NULL,  -- NULL = currently in force
    created_by            BIGINT             NOT NULL,
    creator_role          AS CAST(N'WARD_AUTHORITY' AS NVARCHAR(30)) PERSISTED,
    created_at            DATETIME2          NOT NULL DEFAULT SYSUTCDATETIME(),
    -- The Nghi dinh / Dieu / Khoan behind penalty_amount. AI only drafts wording from this
    -- column; it never invents a legal citation. One source of truth per (ward, type).
    legal_basis           NVARCHAR(500)      NULL,
    CONSTRAINT CK_PenaltyFeeSchedules_Amount
        CHECK (penalty_amount >= 0),
    CONSTRAINT CK_PenaltyFeeSchedules_DateOrder
        CHECK (effective_to IS NULL OR effective_to > effective_from),
    CONSTRAINT FK_PenaltyFeeSchedules_Ward
        FOREIGN KEY (ward_unit_id, ward_unit_type)
        REFERENCES AdministrativeUnits(unit_id, unit_type),
    CONSTRAINT FK_PenaltyFeeSchedules_ViolationType
        FOREIGN KEY (violation_type) REFERENCES ViolationTypes(violation_type_code),
    CONSTRAINT FK_PenaltyFeeSchedules_CreatedBy
        FOREIGN KEY (created_by, creator_role)
        REFERENCES UserAccounts(user_id, role_code)
);
CREATE UNIQUE INDEX UQ_PenaltyFeeSchedules_CurrentRate
    ON PenaltyFeeSchedules(ward_unit_id, violation_type)
    WHERE effective_to IS NULL;

-- WARD-12. A violation is recorded against whatever the officer can actually
-- identify on the spot. Requiring contract_id would make the central case of
-- this system unrecordable: someone occupying the sidewalk with no contract
-- at all. VendorReports (BR-40) already accepts vendor / slot / permit, so the
-- violation it feeds into has to accept the same shapes.
CREATE TABLE Violations (
    violation_id       BIGINT IDENTITY(1,1)  PRIMARY KEY,
    contract_id        BIGINT                NULL,  -- NULL = unlicensed occupation
    slot_id            BIGINT                NULL,  -- where it happened, when no contract exists
    vendor_id          BIGINT                NULL,  -- who, if identifiable
    violation_type     NVARCHAR(50)          NOT NULL,
    description        NVARCHAR(1000)        NULL,
    evidence_url       NVARCHAR(500)         NULL,
    recorded_by        BIGINT                NOT NULL,
    recorder_role         AS CAST(N'WARD_AUTHORITY' AS NVARCHAR(30)) PERSISTED,
    source             NVARCHAR(20)          NOT NULL DEFAULT 'ON_SITE',
    source_report_id   BIGINT                NULL,
    recorded_at        DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Violations_Source
        CHECK (source IN ('ON_SITE','CUSTOMER_REPORT')),
    -- must be pinned to something, or the record is not actionable
    CONSTRAINT CK_Violations_HasTarget
        CHECK (contract_id IS NOT NULL OR slot_id IS NOT NULL OR vendor_id IS NOT NULL),
    CONSTRAINT FK_Violations_Type
        FOREIGN KEY (violation_type) REFERENCES ViolationTypes(violation_type_code),
    CONSTRAINT FK_Violations_Contract
        FOREIGN KEY (contract_id) REFERENCES RentalContracts(contract_id),
    CONSTRAINT FK_Violations_Slot
        FOREIGN KEY (slot_id) REFERENCES SidewalkSlots(slot_id),
    CONSTRAINT FK_Violations_Vendor
        FOREIGN KEY (vendor_id) REFERENCES Vendors(vendor_id),
    CONSTRAINT FK_Violations_RecordedBy
        FOREIGN KEY (recorded_by, recorder_role)
        REFERENCES UserAccounts(user_id, role_code),
    CONSTRAINT FK_Violations_SourceReport
        FOREIGN KEY (source_report_id) REFERENCES VendorReports(report_id)
);
CREATE INDEX IX_Violations_Contract ON Violations(contract_id);
CREATE INDEX IX_Violations_Slot     ON Violations(slot_id);
CREATE INDEX IX_Violations_Vendor   ON Violations(vendor_id);

-- BR-33: amount always looked up from PenaltyFeeSchedules, never entered ad hoc,
-- then frozen here — a later rate change must not rewrite an issued penalty.
CREATE TABLE Penalties (
    penalty_id            BIGINT IDENTITY(1,1)  PRIMARY KEY,
    violation_id          BIGINT                NOT NULL UNIQUE,
    penalty_schedule_id   INT                   NOT NULL,
    amount                DECIMAL(18,0)         NOT NULL,
    penalty_status        NVARCHAR(20)          NOT NULL DEFAULT 'UNPAID',
    created_at            DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    paid_at               DATETIME2             NULL,
    waived_by             BIGINT                NULL,
    waiver_role           AS CAST(N'WARD_AUTHORITY' AS NVARCHAR(30)) PERSISTED,
    waiver_reason         NVARCHAR(500)         NULL,
    waived_at             DATETIME2             NULL,
    -- WARD-13 sanction decision: number and signer. Signer name/title come from the
    -- authenticated officer and UserAccounts.sanction_authority_title, never from the client.
    decision_number       NVARCHAR(50)          NULL,
    signer_name           NVARCHAR(150)         NULL,
    signer_title          NVARCHAR(100)         NULL,
    CONSTRAINT CK_Penalties_Status
        CHECK (penalty_status IN ('UNPAID','PAID','WAIVED','CANCELLED')),
    -- a penalty recorded in error, or forgiven on appeal, has to leave a reason
    -- and a name behind: this is the money side of a public-authority workflow
    CONSTRAINT CK_Penalties_WaiverEvidence
        CHECK (penalty_status NOT IN ('WAIVED','CANCELLED')
               OR (waived_by IS NOT NULL AND waiver_reason IS NOT NULL AND waived_at IS NOT NULL)),
    CONSTRAINT FK_Penalties_Violation
        FOREIGN KEY (violation_id) REFERENCES Violations(violation_id),
    CONSTRAINT FK_Penalties_Schedule
        FOREIGN KEY (penalty_schedule_id) REFERENCES PenaltyFeeSchedules(penalty_schedule_id),
    CONSTRAINT FK_Penalties_WaivedBy
        FOREIGN KEY (waived_by, waiver_role)
        REFERENCES UserAccounts(user_id, role_code)
);
GO
-- Violations may be filed against an unidentified person (slot only). A penalty
-- cannot: Invoices.vendor_id is NOT NULL, so a penalty with nobody to bill would
-- be an unpayable row with no paper trail. The violation stays on record; the
-- penalty waits until the offender is identified and vendor_id is filled in.
CREATE TRIGGER TR_Penalties_RequireIdentifiedOffender
ON Penalties
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN Violations v ON v.violation_id = i.violation_id
        WHERE v.vendor_id IS NULL AND v.contract_id IS NULL
    )
    BEGIN
        RAISERROR (N'Cannot raise a penalty for a violation with no identified vendor or contract.', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END
END;
GO

-- SYS-03: generated per contract from start_date + vendor-selected term (BR-18).
-- Not unique per contract any more: amending a contract supersedes its schedule
-- and generates the next revision, and the superseded rows are the audit trail.
CREATE TABLE FeeSchedules (
    fee_schedule_id   BIGINT IDENTITY(1,1)  PRIMARY KEY,
    contract_id       BIGINT                NOT NULL,
    revision          INT                   NOT NULL DEFAULT 1,
    total_amount      DECIMAL(18,0)         NOT NULL,
    generated_at      DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    superseded_at     DATETIME2             NULL,
    CONSTRAINT UQ_FeeSchedules_ContractRevision
        UNIQUE (contract_id, revision),
    CONSTRAINT FK_FeeSchedules_Contract
        FOREIGN KEY (contract_id) REFERENCES RentalContracts(contract_id)
);
CREATE UNIQUE INDEX UQ_FeeSchedules_CurrentPerContract
    ON FeeSchedules(contract_id)
    WHERE superseded_at IS NULL;

-- FEE-01/02
CREATE TABLE FeeScheduleItems (
    fee_item_id        BIGINT IDENTITY(1,1)  PRIMARY KEY,
    fee_schedule_id    BIGINT                NOT NULL,
    due_date           DATE                  NOT NULL,
    amount             DECIMAL(18,0)         NOT NULL,
    item_status         NVARCHAR(20)          NOT NULL DEFAULT 'PENDING',
    paid_at              DATETIME2             NULL,
    CONSTRAINT CK_FeeScheduleItems_Status
        CHECK (item_status IN ('PENDING','PAID','OVERDUE')),
    CONSTRAINT FK_FeeScheduleItems_Schedule
        FOREIGN KEY (fee_schedule_id) REFERENCES FeeSchedules(fee_schedule_id)
);
CREATE INDEX IX_FeeScheduleItems_DueDate ON FeeScheduleItems(due_date, item_status);

-- FEE-03, SYS-05: issued only after a confirmed payment (BR-32)
CREATE TABLE Invoices (
    invoice_id       BIGINT IDENTITY(1,1)  PRIMARY KEY,
    invoice_number   NVARCHAR(40)          NOT NULL UNIQUE,
    fee_item_id      BIGINT                NULL,
    penalty_id       BIGINT                NULL,
    vendor_id        BIGINT                NOT NULL,
    amount           DECIMAL(18,0)         NOT NULL,
    issued_at         DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    -- exactly one source: an invoice covers a fee instalment or a penalty,
    -- never both, or the amount cannot be reconciled to a single obligation
    CONSTRAINT CK_Invoices_ExactlyOneSource
        CHECK (
            (CASE WHEN fee_item_id IS NULL THEN 0 ELSE 1 END)
          + (CASE WHEN penalty_id  IS NULL THEN 0 ELSE 1 END) = 1
        ),
    CONSTRAINT FK_Invoices_FeeItem
        FOREIGN KEY (fee_item_id) REFERENCES FeeScheduleItems(fee_item_id),
    CONSTRAINT FK_Invoices_Penalty
        FOREIGN KEY (penalty_id) REFERENCES Penalties(penalty_id),
    CONSTRAINT FK_Invoices_Vendor
        FOREIGN KEY (vendor_id) REFERENCES Vendors(vendor_id)
);

-- SYS-04: idempotent callback processing (BR-37). order_id FK added later
-- (ALTER TABLE) once the Phase 2 Orders table exists.
CREATE TABLE PaymentTransactions (
    transaction_id         BIGINT IDENTITY(1,1)  PRIMARY KEY,
    idempotency_key        NVARCHAR(100)         NOT NULL UNIQUE,
    payment_purpose         NVARCHAR(20)          NOT NULL,
    fee_item_id             BIGINT                NULL,
    penalty_id              BIGINT                NULL,
    order_id                BIGINT                NULL,  -- FK added at end of script
    provider                 NVARCHAR(30)          NOT NULL,
    provider_reference       NVARCHAR(100)         NULL,
    amount                    DECIMAL(18,0)         NOT NULL,
    transaction_status       NVARCHAR(20)          NOT NULL DEFAULT 'PENDING',
    callback_received_at    DATETIME2             NULL,
    created_at                DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_PaymentTransactions_Purpose
        CHECK (payment_purpose IN ('RENTAL_FEE','PENALTY','ORDER')),
    CONSTRAINT CK_PaymentTransactions_Provider
        CHECK (provider IN ('MOMO','ZALOPAY')),
    CONSTRAINT CK_PaymentTransactions_Status
        CHECK (transaction_status IN ('PENDING','SUCCESS','FAILED')),
    -- payment_purpose must agree with which target column is filled; without
    -- this a RENTAL_FEE row can carry a penalty_id, or no target at all
    CONSTRAINT CK_PaymentTransactions_PurposeMatchesTarget
        CHECK (
            (payment_purpose = 'RENTAL_FEE' AND fee_item_id IS NOT NULL AND penalty_id  IS NULL AND order_id    IS NULL)
         OR (payment_purpose = 'PENALTY'    AND penalty_id  IS NOT NULL AND fee_item_id IS NULL AND order_id    IS NULL)
         OR (payment_purpose = 'ORDER'      AND order_id    IS NOT NULL AND fee_item_id IS NULL AND penalty_id  IS NULL)
        ),
    CONSTRAINT FK_PaymentTransactions_FeeItem
        FOREIGN KEY (fee_item_id) REFERENCES FeeScheduleItems(fee_item_id),
    CONSTRAINT FK_PaymentTransactions_Penalty
        FOREIGN KEY (penalty_id) REFERENCES Penalties(penalty_id)
);

-- SYS-04. PaymentTransactions.idempotency_key stops a callback being applied
-- twice, but it keeps nothing of what the provider actually sent. When MoMo or
-- ZaloPay disputes a payment months later, the signed payload is the evidence,
-- so every callback is stored whether or not it matched a transaction and
-- whether or not its signature checked out.
CREATE TABLE PaymentCallbackEvents (
    callback_event_id    BIGINT IDENTITY(1,1)  PRIMARY KEY,
    provider             NVARCHAR(30)          NOT NULL,
    provider_reference   NVARCHAR(100)         NULL,
    transaction_id       BIGINT                NULL,  -- NULL when nothing matched
    raw_payload          NVARCHAR(MAX)         NOT NULL,
    signature_valid      BIT                   NOT NULL,
    processing_result    NVARCHAR(20)          NOT NULL,
    received_at          DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_PaymentCallbackEvents_Provider
        CHECK (provider IN ('MOMO','ZALOPAY')),
    CONSTRAINT CK_PaymentCallbackEvents_Result
        CHECK (processing_result IN ('APPLIED','DUPLICATE','UNMATCHED','REJECTED')),
    CONSTRAINT FK_PaymentCallbackEvents_Transaction
        FOREIGN KEY (transaction_id) REFERENCES PaymentTransactions(transaction_id)
);
CREATE INDEX IX_PaymentCallbackEvents_Reference
    ON PaymentCallbackEvents(provider, provider_reference);


/* ============================================================
   6. PLATFORM ADMINISTRATION & SYSTEM (ADM, SYS)
   ============================================================ */

CREATE TABLE FoodCategories (
    category_id     INT IDENTITY(1,1)  PRIMARY KEY,
    category_name   NVARCHAR(100)      NOT NULL UNIQUE,
    created_by       BIGINT             NOT NULL,
    creator_role          AS CAST(N'PLATFORM_ADMIN' AS NVARCHAR(30)) PERSISTED,
    CONSTRAINT FK_FoodCategories_CreatedBy
        FOREIGN KEY (created_by, creator_role)
        REFERENCES UserAccounts(user_id, role_code)
);

CREATE TABLE Notifications (
    notification_id       BIGINT IDENTITY(1,1)  PRIMARY KEY,
    user_id                BIGINT                NOT NULL,
    notification_type     NVARCHAR(50)          NOT NULL,
    title                   NVARCHAR(200)         NOT NULL,
    body                    NVARCHAR(1000)        NOT NULL,
    related_entity_type    NVARCHAR(50)          NULL,
    related_entity_id      BIGINT                NULL,
    is_read                  BIT                   NOT NULL DEFAULT 0,
    sent_at                  DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_Notifications_User
        FOREIGN KEY (user_id) REFERENCES UserAccounts(user_id)
);
CREATE INDEX IX_Notifications_User_Read ON Notifications(user_id, is_read);

-- BR-46: audit trail for registration/rental/fee/penalty/permit state changes
CREATE TABLE AuditLogs (
    audit_id         BIGINT IDENTITY(1,1)  PRIMARY KEY,
    actor_user_id    BIGINT                NOT NULL,
    action           NVARCHAR(100)         NOT NULL,
    entity_type      NVARCHAR(50)          NOT NULL,
    entity_id        BIGINT                NOT NULL,
    details          NVARCHAR(MAX)         NULL,
    created_at        DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_AuditLogs_Actor
        FOREIGN KEY (actor_user_id) REFERENCES UserAccounts(user_id)
);
CREATE INDEX IX_AuditLogs_Entity ON AuditLogs(entity_type, entity_id);

-- BR-41/BR-42: every AI Core Extension / AI Marketplace suggestion is logged
-- and labelled; acceptance/override is recorded but never auto-applied.
CREATE TABLE AIAssistanceLogs (
    ai_log_id      BIGINT IDENTITY(1,1)  PRIMARY KEY,
    feature_code   NVARCHAR(20)          NOT NULL,  -- e.g. AIC-01, AIB-01, AIP-02
    entity_type    NVARCHAR(50)          NOT NULL,
    entity_id      BIGINT                NOT NULL,
    ai_output      NVARCHAR(MAX)         NOT NULL,
    confidence     DECIMAL(5,2)          NULL,
    reviewed_by    BIGINT                NULL,
    accepted       BIT                   NULL,
    created_at      DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_AIAssistanceLogs_Reviewer
        FOREIGN KEY (reviewed_by) REFERENCES UserAccounts(user_id)
);

-- ADM-07, and the download side of WARD-14 / WARD-15 / ADM-06. An export is
-- generated in the background and collected later, so the request needs a row
-- of its own to hold the status and the resulting file.
CREATE TABLE ReportExports (
    export_id        BIGINT IDENTITY(1,1)  PRIMARY KEY,
    requested_by     BIGINT                NOT NULL,
    report_type      NVARCHAR(50)          NOT NULL,
    parameters       NVARCHAR(MAX)         NULL,  -- JSON: date range, ward, filters
    export_status    NVARCHAR(20)          NOT NULL DEFAULT 'PENDING',
    file_url         NVARCHAR(500)         NULL,
    failure_reason   NVARCHAR(500)         NULL,
    requested_at     DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    completed_at     DATETIME2             NULL,
    CONSTRAINT CK_ReportExports_Status
        CHECK (export_status IN ('PENDING','READY','FAILED')),
    CONSTRAINT CK_ReportExports_ReadyHasFile
        CHECK (export_status <> 'READY' OR file_url IS NOT NULL),
    CONSTRAINT FK_ReportExports_RequestedBy
        FOREIGN KEY (requested_by) REFERENCES UserAccounts(user_id)
);
CREATE INDEX IX_ReportExports_Requester ON ReportExports(requested_by, requested_at);


/* ============================================================
   7. PHASE 2 — MARKETPLACE (gated by REG + SIDE, BR-48)
   ============================================================ */

-- BR-48 marketplace gate. contract_id was NOT NULL, which silently excluded
-- every ITINERANT vendor from Phase 2: an itinerant vendor by definition rents
-- no fixed slot, so it could never satisfy the column and could never open a
-- storefront. It is now nullable, and TR_Storefronts_Phase2Gate enforces the
-- rule that actually applies: an approved registration, plus an active rental
-- contract only where the registration is for a fixed storefront.
--
-- If the report intends to keep itinerant vendors out of the marketplace, make
-- contract_id NOT NULL again and delete the ITINERANT branch of the trigger.
-- BR-48 marketplace gate: an APPROVED registration plus an ACTIVE rental
-- contract. This applies to both vendor types. An itinerant vendor is not a
-- vendor without a slot - SIDE-03B has them apply for open slots from the map
-- and Actor 4 lets them hold several at once. The difference between the two
-- types is only how they reach a slot: FIXED_STOREFRONT applies for the slot
-- adjacent to its declared address (AUTO_ADJACENT), ITINERANT picks any open
-- slot (MANUAL_SELECTED).
--
-- One storefront per registration, pinned to one contract, because a
-- pickup-only order needs a single unambiguous place to collect from. A vendor
-- renting several slots and wanting to trade at each of them needs a
-- registration per storefront (BR-06 allows several per vendor).
CREATE TABLE Storefronts (
    storefront_id         BIGINT IDENTITY(1,1)  PRIMARY KEY,
    registration_id       BIGINT                NOT NULL,
    contract_id           BIGINT                NOT NULL,
    storefront_name       NVARCHAR(180)         NOT NULL,
    description           NVARCHAR(1000)        NULL,
    image_url             NVARCHAR(500)         NULL,
    availability_status   NVARCHAR(20)          NOT NULL DEFAULT 'CLOSED',
    created_at            DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at            DATETIME2             NULL,
    CONSTRAINT CK_Storefronts_Availability
        CHECK (availability_status IN ('OPEN','PAUSED','CLOSED')),
    CONSTRAINT UQ_Storefronts_Registration
        UNIQUE (registration_id),
    -- one rented slot cannot host two storefronts
    CONSTRAINT UQ_Storefronts_Contract
        UNIQUE (contract_id),
    CONSTRAINT FK_Storefronts_Registration
        FOREIGN KEY (registration_id) REFERENCES BusinessRegistrations(registration_id),
    CONSTRAINT FK_Storefronts_Contract
        FOREIGN KEY (contract_id) REFERENCES RentalContracts(contract_id)
);
GO
CREATE TRIGGER TR_Storefronts_Phase2Gate
ON Storefronts
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN BusinessRegistrations r ON r.registration_id = i.registration_id
        WHERE r.registration_status <> 'APPROVED'
    )
    BEGIN
        RAISERROR (N'Storefront requires an APPROVED business registration.', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END

    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN RentalContracts c ON c.contract_id = i.contract_id
        WHERE c.contract_status <> 'ACTIVE'
    )
    BEGIN
        RAISERROR (N'Storefront requires an ACTIVE rental contract.', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END

    -- the contract and the storefront must belong to the same vendor
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN BusinessRegistrations r ON r.registration_id = i.registration_id
        JOIN RentalContracts c       ON c.contract_id     = i.contract_id
        WHERE c.vendor_id <> r.vendor_id
    )
    BEGIN
        RAISERROR (N'Storefront contract belongs to a different vendor.', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END
END;
GO

CREATE TABLE StorefrontBusinessHours (
    hour_id         BIGINT IDENTITY(1,1)  PRIMARY KEY,
    storefront_id   BIGINT                NOT NULL,
    day_of_week     SMALLINT              NOT NULL,
    opens_at        TIME                  NOT NULL,
    closes_at       TIME                  NOT NULL,
    CONSTRAINT CK_StorefrontBusinessHours_Day
        CHECK (day_of_week BETWEEN 1 AND 7),
    CONSTRAINT CK_StorefrontBusinessHours_TimeOrder
        CHECK (opens_at < closes_at),
    CONSTRAINT FK_StorefrontBusinessHours_Storefront
        FOREIGN KEY (storefront_id) REFERENCES Storefronts(storefront_id)
);

CREATE TABLE MenuItems (
    menu_item_id         BIGINT IDENTITY(1,1)  PRIMARY KEY,
    storefront_id         BIGINT                NOT NULL,
    category_id           INT                   NOT NULL,
    item_name             NVARCHAR(180)         NOT NULL,
    description            NVARCHAR(500)         NULL,
    image_url               NVARCHAR(500)         NULL,
    unit_price             DECIMAL(18,0)         NOT NULL,
    availability_status    NVARCHAR(20)          NOT NULL DEFAULT 'AVAILABLE',
    created_at               DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at               DATETIME2             NULL,
    CONSTRAINT CK_MenuItems_Price
        CHECK (unit_price >= 0),
    CONSTRAINT CK_MenuItems_Availability
        CHECK (availability_status IN ('AVAILABLE','SOLD_OUT','HIDDEN','ARCHIVED')),
    CONSTRAINT FK_MenuItems_Storefront
        FOREIGN KEY (storefront_id) REFERENCES Storefronts(storefront_id),
    CONSTRAINT FK_MenuItems_Category
        FOREIGN KEY (category_id) REFERENCES FoodCategories(category_id)
);
CREATE INDEX IX_MenuItems_Storefront ON MenuItems(storefront_id);

-- BR-52: a cart holds items from exactly one storefront (enforced by storefront_id here)
CREATE TABLE ShoppingCarts (
    cart_id             BIGINT IDENTITY(1,1)  PRIMARY KEY,
    customer_user_id    BIGINT                NOT NULL,
    storefront_id        BIGINT                NOT NULL,
    cart_status           NVARCHAR(20)          NOT NULL DEFAULT 'ACTIVE',
    created_at              DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_ShoppingCarts_Status
        CHECK (cart_status IN ('ACTIVE','CHECKED_OUT','ABANDONED')),
    CONSTRAINT FK_ShoppingCarts_Customer
        FOREIGN KEY (customer_user_id) REFERENCES UserAccounts(user_id),
    CONSTRAINT FK_ShoppingCarts_Storefront
        FOREIGN KEY (storefront_id) REFERENCES Storefronts(storefront_id)
);
-- At most one ACTIVE cart per customer per storefront:
CREATE UNIQUE INDEX UQ_ShoppingCarts_ActivePerStorefront
    ON ShoppingCarts(customer_user_id, storefront_id)
    WHERE cart_status = 'ACTIVE';

CREATE TABLE ShoppingCartItems (
    cart_item_id     BIGINT IDENTITY(1,1)  PRIMARY KEY,
    cart_id          BIGINT                NOT NULL,
    menu_item_id     BIGINT                NOT NULL,
    quantity         INT                   NOT NULL,
    note             NVARCHAR(300)         NULL,
    CONSTRAINT CK_ShoppingCartItems_Quantity
        CHECK (quantity >= 1),
    CONSTRAINT FK_ShoppingCartItems_Cart
        FOREIGN KEY (cart_id) REFERENCES ShoppingCarts(cart_id),
    CONSTRAINT FK_ShoppingCartItems_MenuItem
        FOREIGN KEY (menu_item_id) REFERENCES MenuItems(menu_item_id)
);

-- ORD-01: pickup-only lifecycle. An order is created as PENDING_PAYMENT and
-- only becomes PLACED once a payment succeeds (BR-54) — PaymentTransactions
-- references order_id, so the order necessarily exists before the payment does.
CREATE TABLE Orders (
    order_id                BIGINT IDENTITY(1,1)  PRIMARY KEY,
    order_code               NVARCHAR(30)          NOT NULL UNIQUE,
    customer_user_id         BIGINT                NOT NULL,
    storefront_id             BIGINT                NOT NULL,
    order_status               NVARCHAR(20)          NOT NULL DEFAULT 'PENDING_PAYMENT',
    subtotal_amount           DECIMAL(18,0)         NOT NULL,
    total_amount               DECIMAL(18,0)         NOT NULL,
    rejection_reason          NVARCHAR(500)         NULL,
    placed_at                   DATETIME2             NULL,
    completed_at                 DATETIME2             NULL,
    created_at                   DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    -- The pickup address as it read when the order was placed; a later address change on
    -- the registration must not rewrite it.
    storefront_address_snapshot  NVARCHAR(500)         NULL,
    CONSTRAINT CK_Orders_Status
        CHECK (order_status IN
            ('PENDING_PAYMENT','PLACED','ACCEPTED','REJECTED','PREPARING','READY_FOR_PICKUP','COMPLETED','CANCELLED')),
    CONSTRAINT FK_Orders_Customer
        FOREIGN KEY (customer_user_id) REFERENCES UserAccounts(user_id),
    CONSTRAINT FK_Orders_Storefront
        FOREIGN KEY (storefront_id) REFERENCES Storefronts(storefront_id)
);
CREATE INDEX IX_Orders_Customer ON Orders(customer_user_id);
CREATE INDEX IX_Orders_Storefront ON Orders(storefront_id);

CREATE TABLE OrderItems (
    order_item_id           BIGINT IDENTITY(1,1)  PRIMARY KEY,
    order_id                 BIGINT                NOT NULL,
    menu_item_id              BIGINT                NOT NULL,
    item_name_snapshot        NVARCHAR(180)         NOT NULL,
    unit_price_snapshot       DECIMAL(18,0)         NOT NULL,
    quantity                   INT                   NOT NULL,
    note                        NVARCHAR(300)         NULL,
    CONSTRAINT CK_OrderItems_Quantity
        CHECK (quantity >= 1),
    CONSTRAINT FK_OrderItems_Order
        FOREIGN KEY (order_id) REFERENCES Orders(order_id),
    CONSTRAINT FK_OrderItems_MenuItem
        FOREIGN KEY (menu_item_id) REFERENCES MenuItems(menu_item_id)
);

-- ORD-02. order_status holds only where the order is now; the tracking screen
-- has to show when it was accepted, when it started being prepared and when it
-- became ready. AuditLogs is the ward-side compliance trail and is not what a
-- customer-facing timeline should read from.
CREATE TABLE OrderStatusHistory (
    history_id     BIGINT IDENTITY(1,1)  PRIMARY KEY,
    order_id       BIGINT                NOT NULL,
    from_status    NVARCHAR(20)          NULL,  -- NULL on the first row
    to_status      NVARCHAR(20)          NOT NULL,
    changed_by     BIGINT                NULL,  -- NULL = system, e.g. a payment callback
    note           NVARCHAR(500)         NULL,
    changed_at     DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_OrderStatusHistory_ToStatus
        CHECK (to_status IN
            ('PENDING_PAYMENT','PLACED','ACCEPTED','REJECTED','PREPARING','READY_FOR_PICKUP','COMPLETED','CANCELLED')),
    CONSTRAINT FK_OrderStatusHistory_Order
        FOREIGN KEY (order_id) REFERENCES Orders(order_id),
    CONSTRAINT FK_OrderStatusHistory_ChangedBy
        FOREIGN KEY (changed_by) REFERENCES UserAccounts(user_id)
);
CREATE INDEX IX_OrderStatusHistory_Order ON OrderStatusHistory(order_id, changed_at);

-- REV-01/02: review only after COMPLETED (BR-56), one review per order.
CREATE TABLE Reviews (
    review_id            BIGINT IDENTITY(1,1)  PRIMARY KEY,
    order_id              BIGINT                NOT NULL UNIQUE,
    customer_user_id      BIGINT                NOT NULL,
    rating                 SMALLINT              NOT NULL,
    review_text            NVARCHAR(1000)        NULL,
    created_at               DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    edited_at                DATETIME2             NULL,
    CONSTRAINT CK_Reviews_Rating
        CHECK (rating BETWEEN 1 AND 5),
    CONSTRAINT FK_Reviews_Order
        FOREIGN KEY (order_id) REFERENCES Orders(order_id),
    CONSTRAINT FK_Reviews_Customer
        FOREIGN KEY (customer_user_id) REFERENCES UserAccounts(user_id)
);

-- REV-04
CREATE TABLE Complaints (
    complaint_id                BIGINT IDENTITY(1,1)  PRIMARY KEY,
    order_id                     BIGINT                NOT NULL,
    customer_user_id             BIGINT                NOT NULL,
    complaint_type                NVARCHAR(20)          NOT NULL,
    description                    NVARCHAR(1000)        NOT NULL,
    requested_refund_amount      DECIMAL(18,0)         NULL,
    status                         NVARCHAR(20)          NOT NULL DEFAULT 'OPEN',
    resolved_by                    BIGINT                NULL,
    resolver_role         AS CAST(N'PLATFORM_ADMIN' AS NVARCHAR(30)) PERSISTED,
    resolution_notes               NVARCHAR(1000)        NULL,
    created_at                      DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    resolved_at                     DATETIME2             NULL,
    CONSTRAINT CK_Complaints_Type
        CHECK (complaint_type IN ('COMPLAINT','REFUND_REQUEST')),
    CONSTRAINT CK_Complaints_Status
        CHECK (status IN ('OPEN','UNDER_REVIEW','RESOLVED','REJECTED')),
    CONSTRAINT FK_Complaints_Order
        FOREIGN KEY (order_id) REFERENCES Orders(order_id),
    CONSTRAINT FK_Complaints_Customer
        FOREIGN KEY (customer_user_id) REFERENCES UserAccounts(user_id),
    CONSTRAINT FK_Complaints_ResolvedBy
        FOREIGN KEY (resolved_by, resolver_role)
        REFERENCES UserAccounts(user_id, role_code)
);

-- PAY-04. The marketplace takes money up front and hands goods over at the
-- stall, so every REJECTED order (SORD-01) and every CANCELLED paid order
-- (ORD-03) owes the customer their money back, plus whatever ADM-05 awards on
-- a complaint. Complaints.requested_refund_amount records the asking; this
-- table records the paying.
--
-- A refund is a separate movement, not a status on PaymentTransactions: the
-- money did come in, that row stays true, and the provider issues a distinct
-- reference for the return that has to be stored somewhere.
CREATE TABLE RefundTransactions (
    refund_id                   BIGINT IDENTITY(1,1)  PRIMARY KEY,
    order_id                    BIGINT                NOT NULL,
    payment_transaction_id      BIGINT                NOT NULL,
    complaint_id                BIGINT                NULL,  -- NULL = automatic on reject/cancel
    idempotency_key             NVARCHAR(100)         NOT NULL UNIQUE,
    amount                      DECIMAL(18,0)         NOT NULL,
    refund_reason               NVARCHAR(30)          NOT NULL,
    provider                    NVARCHAR(30)          NOT NULL,
    provider_refund_reference   NVARCHAR(100)         NULL,
    refund_status               NVARCHAR(20)          NOT NULL DEFAULT 'PENDING',
    requested_at                DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    completed_at                DATETIME2             NULL,
    CONSTRAINT CK_RefundTransactions_Reason
        CHECK (refund_reason IN ('ORDER_REJECTED','ORDER_CANCELLED','COMPLAINT_RESOLVED')),
    CONSTRAINT CK_RefundTransactions_Provider
        CHECK (provider IN ('MOMO','ZALOPAY')),
    CONSTRAINT CK_RefundTransactions_Status
        CHECK (refund_status IN ('PENDING','SUCCESS','FAILED')),
    CONSTRAINT CK_RefundTransactions_AmountPositive
        CHECK (amount > 0),
    CONSTRAINT CK_RefundTransactions_SuccessIsComplete
        CHECK (refund_status <> 'SUCCESS'
               OR (completed_at IS NOT NULL AND provider_refund_reference IS NOT NULL)),
    -- a refund raised from a complaint must say which one
    CONSTRAINT CK_RefundTransactions_ComplaintReason
        CHECK ((refund_reason = 'COMPLAINT_RESOLVED' AND complaint_id IS NOT NULL)
            OR (refund_reason <> 'COMPLAINT_RESOLVED' AND complaint_id IS NULL)),
    CONSTRAINT FK_RefundTransactions_Order
        FOREIGN KEY (order_id) REFERENCES Orders(order_id),
    CONSTRAINT FK_RefundTransactions_Payment
        FOREIGN KEY (payment_transaction_id) REFERENCES PaymentTransactions(transaction_id),
    CONSTRAINT FK_RefundTransactions_Complaint
        FOREIGN KEY (complaint_id) REFERENCES Complaints(complaint_id)
);
CREATE INDEX IX_RefundTransactions_Order ON RefundTransactions(order_id);
GO
-- Refunding more than was taken would be a hole straight through the ledger,
-- and partial refunds are legitimate (ADM-05 can award part of an order), so
-- the cap is on the running total rather than on any single row.
CREATE TRIGGER TR_RefundTransactions_NotMoreThanPaid
ON RefundTransactions
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1
        FROM (SELECT DISTINCT payment_transaction_id FROM inserted) i
        JOIN PaymentTransactions p ON p.transaction_id = i.payment_transaction_id
        CROSS APPLY (
            SELECT SUM(r.amount) AS refunded
            -- same reason as TR_RentalContracts_NoOverlap: a running-total cap
            -- is a write-skew invariant and needs the range locked, or two
            -- concurrent refunds both pass and together overdraw the payment
            FROM RefundTransactions r WITH (UPDLOCK, HOLDLOCK)
            WHERE r.payment_transaction_id = i.payment_transaction_id
              AND r.refund_status IN ('PENDING','SUCCESS')
        ) t
        WHERE t.refunded > p.amount
    )
    BEGIN
        RAISERROR (N'Total refunds would exceed the amount actually paid.', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END
END;
GO

-- REV-03 / ADM-03/04: content moderation queue for Phase 2 marketplace content
CREATE TABLE ReportedContent (
    content_report_id     BIGINT IDENTITY(1,1)  PRIMARY KEY,
    content_type           NVARCHAR(20)          NOT NULL,
    content_id              BIGINT                NOT NULL,
    reporter_user_id       BIGINT                NOT NULL,
    reason                   NVARCHAR(500)         NOT NULL,
    status                    NVARCHAR(20)          NOT NULL DEFAULT 'PENDING',
    reviewed_by               BIGINT                NULL,
    reviewer_role         AS CAST(N'PLATFORM_ADMIN' AS NVARCHAR(30)) PERSISTED,
    reviewed_at                 DATETIME2             NULL,
    created_at                   DATETIME2             NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_ReportedContent_Type
        CHECK (content_type IN ('STOREFRONT','MENU_ITEM','REVIEW')),
    CONSTRAINT CK_ReportedContent_Status
        CHECK (status IN ('PENDING','HIDDEN','DISMISSED')),
    CONSTRAINT FK_ReportedContent_Reporter
        FOREIGN KEY (reporter_user_id) REFERENCES UserAccounts(user_id),
    CONSTRAINT FK_ReportedContent_Reviewer
        FOREIGN KEY (reviewed_by, reviewer_role)
        REFERENCES UserAccounts(user_id, role_code)
);


/* ============================================================
   8. DEFERRED FOREIGN KEYS (forward references)
   ============================================================ */

ALTER TABLE PaymentTransactions
    ADD CONSTRAINT FK_PaymentTransactions_Order
    FOREIGN KEY (order_id) REFERENCES Orders(order_id);


/* ============================================================
   9. VIEWS
   ============================================================ */
GO
-- WARD-11 and BUY-02 ask one question: is the QR in front of me good right now?
--
-- DigitalPermits.permit_status alone cannot answer it. It records what the ward
-- did to the permit (WARD-13) and knows nothing about the contract underneath,
-- so a vendor who returned their slot kept a permit still reading ACTIVE and the
-- QR still scanned as valid.
--
-- The fix is not to copy contract_status into the permit and keep two truths in
-- step. Validity is derived from both, plus today's date, so it is computed here
-- and there is nothing left to synchronise, nothing for a service to forget, and
-- no trigger writing to a table the caller did not ask to touch.
--
-- Order matters: revocation beats suspension, suspension beats a date check.
-- WARD-11 and BUY-02 must read effective_status from this view, never
-- DigitalPermits.permit_status.
CREATE VIEW vw_PermitValidity AS
SELECT
    p.permit_id,
    p.contract_id,
    p.qr_payload,
    c.slot_id,
    c.vendor_id,
    c.start_date,
    c.end_date,
    p.permit_status,
    c.contract_status,
    CASE
        WHEN p.permit_status   = 'REVOKED'                    THEN 'REVOKED'
        WHEN c.contract_status IN ('CANCELLED','REVOKED')      THEN 'REVOKED'
        WHEN p.permit_status   = 'SUSPENDED'                   THEN 'SUSPENDED'
        WHEN c.contract_status = 'SUSPENDED'                   THEN 'SUSPENDED'
        WHEN c.contract_status = 'EXPIRED'                     THEN 'EXPIRED'
        WHEN CAST(SYSUTCDATETIME() AS DATE) > c.end_date       THEN 'EXPIRED'
        WHEN CAST(SYSUTCDATETIME() AS DATE) < c.start_date     THEN 'NOT_YET_VALID'
        ELSE 'VALID'
    END AS effective_status
FROM DigitalPermits p
JOIN RentalContracts c ON c.contract_id = p.contract_id;
GO
-- Two rating populations exist and they are not comparable: VendorComments is
-- open to anyone who met the vendor on the street (Phase 1, unverified), while
-- Reviews requires a COMPLETED order (Phase 2, verified purchase). Averaging
-- them together would let unverified opinion move a verified score, so this
-- view returns them side by side and lets the UI decide what to show.
-- Display rule: verified_rating is the headline; community_rating is context.
CREATE VIEW vw_VendorRatings AS
SELECT
    v.vendor_id,
    ISNULL(ver.verified_count, 0)  AS verified_count,
    ver.verified_rating,
    ISNULL(com.community_count, 0) AS community_count,
    com.community_rating
FROM Vendors v
OUTER APPLY (
    SELECT COUNT(*) AS verified_count,
           CAST(AVG(CAST(r.rating AS DECIMAL(4,2))) AS DECIMAL(4,2)) AS verified_rating
    FROM Reviews r
    JOIN Orders o                 ON o.order_id       = r.order_id
    JOIN Storefronts s            ON s.storefront_id  = o.storefront_id
    JOIN BusinessRegistrations br ON br.registration_id = s.registration_id
    WHERE br.vendor_id = v.vendor_id
) ver
OUTER APPLY (
    SELECT COUNT(*) AS community_count,
           CAST(AVG(CAST(c.rating AS DECIMAL(4,2))) AS DECIMAL(4,2)) AS community_rating
    FROM VendorComments c
    WHERE c.vendor_id = v.vendor_id
      AND c.rating IS NOT NULL
) com;
GO


/* ============================================================
   10. REFERENCE DATA
   The rows the application cannot function without, independent of any demo
   scenario: the role vocabulary, the administrative hierarchy every ward-scoped
   table hangs off, and the violation catalogue WARD-03 prices.

   Everything else -- pricing zones, slots, fee schedules, food categories --
   carries a created_by pointing at a real officer or administrator, so it cannot
   exist before accounts do and lives in db/StreetBiz_Demo_Seed.sql instead.

   Add a role, a ward or a violation type HERE, not in a side script.
   ============================================================ */
GO
SET NOCOUNT ON;
GO

-- Roles (AUTH). GUEST is deliberately absent -- see the schema comment: an
-- unauthenticated visitor is an actor, never a row.
INSERT INTO Roles (role_code, role_name) VALUES
    ('CUSTOMER',       N'Khách hàng'),
    ('VENDOR',         N'Hộ kinh doanh'),
    ('WARD_AUTHORITY', N'Cán bộ phường'),
    ('PLATFORM_ADMIN', N'Quản trị hệ thống');
GO

-- AdministrativeUnits. Ids are pinned with IDENTITY_INSERT because docs, seeds and
-- scripts/e2e-auth-onboarding.sh refer to wards 10/11/12 by number.
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
GO

-- ViolationTypes (WARD-12 catalogue). Natural key: the code is what an officer picks on
-- the spot and what PenaltyFeeSchedules prices. The first ten are the operational
-- catalogue; the last five are the legal-citation set the ward compliance module and its
-- AI classifier (AiComplianceService) map onto.
INSERT INTO ViolationTypes (violation_type_code, description, is_active) VALUES
    ('NO_PERMIT',                 N'Kinh doanh trên vỉa hè không có giấy phép',                 1),
    ('OUTSIDE_SLOT',              N'Bày bán vượt ra ngoài phạm vi ô được thuê',                 1),
    ('OUTSIDE_HOURS',             N'Kinh doanh ngoài khung giờ cho phép của khu vực',           1),
    ('BLOCK_PEDESTRIAN',          N'Cản trở lối đi bộ dành cho người đi đường',                 1),
    ('BLOCK_EMERGENCY',           N'Cản trở lối thoát hiểm, trụ nước chữa cháy hoặc trạm điện', 1),
    ('HYGIENE_VIOLATION',         N'Vi phạm quy định vệ sinh an toàn thực phẩm',                1),
    ('WASTE_DISPOSAL',            N'Xả rác, nước thải không đúng nơi quy định',                 1),
    ('NOISE_VIOLATION',           N'Gây tiếng ồn vượt mức cho phép',                            1),
    ('UNAUTHORIZED_TRANSFER',     N'Tự ý sang nhượng ô cho người khác',                         1),
    ('PERMIT_TAMPERING',          N'Sử dụng giấy phép giả mạo hoặc đã bị thu hồi',              1),
    ('UNAUTHORIZED_BUSINESS_USE', N'Sử dụng trái phép lòng đường, vỉa hè để kinh doanh (không phép hoặc sai nội dung)', 1),
    ('EXPIRED_OR_INVALID_PERMIT', N'Giấy phép sử dụng tạm thời vỉa hè hết hiệu lực hoặc sử dụng sai nội dung giấy phép', 1),
    ('STREET_VENDING_RESTRICTED', N'Bán hàng rong tại tuyến phố cấm',                           1),
    ('HYGIENE_LITTERING',         N'Vứt, thải, để rác thải sinh hoạt trên vỉa hè, lòng đường',  1),
    ('OBSTRUCT_PUBLIC_ORDER',     N'Đổ rác, vật cản gây mất an ninh trật tự công cộng',         1);
GO


/* ============================================================
   11. EF MIGRATION HISTORY
   StreetBizDB is database-first: this file is the schema, EF never creates or alters it
   (docs/database.md). The rows are stamped here so EF never believes it has work to do:
   InitialBaseline has an intentionally empty Up() (the schema predates EF) and
   AddOrderStorefrontAddressSnapshot is already part of the Orders table above.
   ProductVersion must match the EF Core version in Directory.Build.props / the csproj.
   ============================================================ */
CREATE TABLE [__EFMigrationsHistory] (
    [MigrationId]    nvarchar(150) NOT NULL,
    [ProductVersion] nvarchar(32)  NOT NULL,
    CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
);
GO
INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES
    (N'20260909122414_InitialBaseline',                   N'8.0.31'),
    (N'20260919163132_AddOrderStorefrontAddressSnapshot', N'8.0.31');
GO


/* ============================================================
   Table count summary
   ------------------------------------------------------------
   Identity & Reference ......... 6  (AdministrativeUnits, Roles,
                                      UserAccounts, UserSessions,
                                      OtpChallenges, UserDevices)
   Vendor Registration ........... 5  (Vendors, BusinessRegistrations,
                                      RegistrationEvidence,
                                      BusinessRegistrationHouseholdMembers,
                                      KycVerificationResults)
   Sidewalk Slot & Rental ........ 12 (PricingZones, SidewalkSlots,
                                      ZoneFeeComponents, StreetFeatures,
                                      SlotHolds,
                                      RentalApplications, RentalContracts,
                                      DigitalPermits, PermitScanLogs,
                                      SlotTransferRequests, RenewalRequests,
                                      AddressChangeRequests)
   Buyer Trust & Verification .... 2  (VendorComments, VendorReports)
   Fee, Penalty & Invoicing ...... 9  (ViolationTypes, PenaltyFeeSchedules,
                                      Violations, Penalties, FeeSchedules,
                                      FeeScheduleItems, Invoices,
                                      PaymentTransactions,
                                      PaymentCallbackEvents)
   Platform Admin & System ....... 5  (FoodCategories, Notifications,
                                      AuditLogs, AIAssistanceLogs,
                                      ReportExports)
                                      -- FoodCategories shared with Phase 2 menus/search
   Phase 2 Marketplace ........... 11 (Storefronts, StorefrontBusinessHours,
                                      MenuItems, ShoppingCarts,
                                      ShoppingCartItems, Orders, OrderItems,
                                      OrderStatusHistory, Reviews, Complaints,
                                      RefundTransactions)
   Phase 2 Moderation ............ 1  (ReportedContent)
   ------------------------------------------------------------
   TOTAL: 51 tables (plus __EFMigrationsHistory), 5 triggers, 2 views

   Use-case coverage: the 70 Core and 31 Phase 2 use cases of the actor
   specification. AIAssistanceLogs, RegistrationEvidence.ocr_extracted_data
   and VendorReports.ai_extracted_location have no matching use case in that
   list and are left in place pending a decision on the AI scope.
   ============================================================ */
