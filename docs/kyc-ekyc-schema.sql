/* ============================================================
   eKYC: CCCD two-sided OCR autofill + face match (REG-02)

   Context: the registration wizard previously asked the vendor to type every
   Mẫu số 01 owner field by hand and to upload a single, untyped
   IDENTITY_DOCUMENT photo. Two-sided capture is required because the fields
   live on different sides of a CCCD:
     - front: id, name, dob, sex, nationality, address (thường trú)
     - back:  ethnicity (dân tộc), issue_date (ngày cấp), issue_loc (nơi cấp)
   A portrait selfie is additionally captured so FPT.AI Facematch can compare
   the applicant against the photo printed on the card.

   Adds:
     - CK_RegistrationEvidence_Type: two new evidence types.
       IDENTITY_DOCUMENT keeps its meaning (CCCD mặt trước) so existing rows
       stay valid; IDENTITY_DOCUMENT_BACK and PORTRAIT_SELFIE are new.
     - KycVerificationResults: server-recorded outcome of each AI check.
       The score is written where it is computed (server side) and read back
       by the ward officer's screen -- never round-tripped through the client,
       which could otherwise claim any similarity it liked. registration_id is
       NULL until the wizard actually creates the registration (the checks run
       earlier, while the form is still being filled in), then backfilled.

   These results are decision support only (BR-41): they never approve, reject
   or change any state on their own. DecideEnrollmentAsync still requires the
   officer's own ConfirmIdentityAsync regardless of how high a score is.

   Idempotent: every statement is guarded, so re-running is a no-op.
   Apply by hand (see docs/migration-guide.md -- the API never runs schema
   changes), then mirror into the canonical schema script.
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ---- Evidence types: CCCD mặt sau + ảnh chân dung ---- */
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_RegistrationEvidence_Type')
BEGIN
    DECLARE @table SYSNAME = (
        SELECT TOP 1 t.name
        FROM sys.check_constraints cc
        JOIN sys.tables t ON cc.parent_object_id = t.object_id
        WHERE cc.name = 'CK_RegistrationEvidence_Type');

    EXEC('ALTER TABLE ' + @table + ' DROP CONSTRAINT CK_RegistrationEvidence_Type');
    EXEC('ALTER TABLE ' + @table + ' ADD CONSTRAINT CK_RegistrationEvidence_Type CHECK (
        evidence_type IN (''IDENTITY_DOCUMENT'', ''IDENTITY_DOCUMENT_BACK'', ''PORTRAIT_SELFIE'',
                          ''BUSINESS_LICENSE'', ''ADDRESS_PROOF'', ''OTHER''))');
END
GO

/* ---- Server-recorded AI check outcomes (decision support only, BR-41) ---- */
IF OBJECT_ID('KycVerificationResults', 'U') IS NULL
CREATE TABLE KycVerificationResults (
    kyc_result_id        BIGINT IDENTITY(1,1) PRIMARY KEY,
    user_id              BIGINT NOT NULL
        CONSTRAINT FK_KycVerificationResults_User REFERENCES UserAccounts(user_id),
    /* NULL until the wizard creates the registration these checks belong to. */
    registration_id      BIGINT NULL
        CONSTRAINT FK_KycVerificationResults_Registration REFERENCES BusinessRegistrations(registration_id),
    check_type           NVARCHAR(30)  NOT NULL,   -- 'ID_CARD_OCR' | 'FACE_MATCH'
    provider             NVARCHAR(30)  NOT NULL,   -- 'FPT.AI'
    is_match             BIT           NULL,       -- FACE_MATCH only
    similarity_percent   DECIMAL(5,2)  NULL,       -- FACE_MATCH only
    confidence_percent   INT           NULL,       -- ID_CARD_OCR only
    extracted_id_number  NVARCHAR(12)  NULL,
    warnings             NVARCHAR(1000) NULL,
    created_at           DATETIME2 NOT NULL
        CONSTRAINT DF_KycVerificationResults_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_KycVerificationResults_CheckType
        CHECK (check_type IN ('ID_CARD_OCR', 'FACE_MATCH'))
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_KycVerificationResults_User_Created')
CREATE INDEX IX_KycVerificationResults_User_Created
    ON KycVerificationResults (user_id, created_at DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_KycVerificationResults_Registration')
CREATE INDEX IX_KycVerificationResults_Registration
    ON KycVerificationResults (registration_id) WHERE registration_id IS NOT NULL;
GO
