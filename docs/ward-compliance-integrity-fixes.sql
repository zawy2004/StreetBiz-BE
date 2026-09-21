/* ============================================================
   Ward Review, Permit & Compliance -- integrity fixes

   Follow-up to docs/legal-penalty-schema.sql, after an audit found the
   WardCompliance module (which is the sole implementation of registration
   review, rental approval, contract/permit issuance and sanctioning --
   there is no separate command elsewhere it could have been duplicating)
   had drifted from the real state machines of the Slot/Rental/Permit and
   Auth/VendorRegistration modules it depends on. See
   docs_system/features/ward-review-permit-compliance.md for the full audit.

   Adds:
     - UserAccounts.sanction_authority_title: fixed sanction-signing title
       (e.g. "Chu tich UBND Phuong") configured per account out-of-band by a
       platform admin. SanctionViolationAsync now sources SignerName/
       SignerTitle entirely from the authenticated actor + this column --
       a prior version let the client free-type both, so any Ward Authority
       account could self-declare being the Chairman with nothing to verify
       it. NULL means that account cannot sign sanction decisions.
     - BusinessRegistrations.ai_check_result / ai_checked_at: caches the last
       AI document-check result. A prior version called the AI provider live
       on every GET of a registration's detail page (not just the explicit
       "Quet lai CCCD [AI]" action) -- expensive and pointless since nothing
       had changed between views.

   Idempotent: every statement is guarded, so re-running is a no-op.
   Apply by hand (see docs/migration-guide.md -- the API never runs schema
   changes), then mirror into the canonical schema script.
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ---- UserAccounts: fixed, admin-configured sanction-signing title ---- */
IF COL_LENGTH('UserAccounts', 'sanction_authority_title') IS NULL
    ALTER TABLE UserAccounts ADD sanction_authority_title NVARCHAR(100) NULL;
GO

/* ---- BusinessRegistrations: cached AI document-check result ---- */
IF COL_LENGTH('BusinessRegistrations', 'ai_check_result') IS NULL
    ALTER TABLE BusinessRegistrations ADD ai_check_result NVARCHAR(MAX) NULL;
IF COL_LENGTH('BusinessRegistrations', 'ai_checked_at') IS NULL
    ALTER TABLE BusinessRegistrations ADD ai_checked_at DATETIME2 NULL;
GO
