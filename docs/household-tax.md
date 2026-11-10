# Household tax support (TAX)

StreetBiz helps street vendors and the ward that manages them keep track of **tax-relevant
revenue**. It is a *reference and reminder* tool. **StreetBiz does not file, collect or pay tax on
anyone's behalf**, and every figure it shows carries that disclaimer
(`TaxMessages.Disclaimer`). The household files with the tax office itself; StreetBiz only records
that it did.

> **Read this before relying on any number.** The rules for household businesses changed twice in
> 2026. The values seeded here (a 1-billion-VND no-tax threshold, 3 %/1.5 % food-service rates,
> personal income tax deducting the threshold, a 31 January yearly-notice date) come from news and
> portal summaries, not from the primary legal texts. A platform administrator must check them
> against the current official documents and publish a new policy version if anything differs. The
> food-service rates in particular are **unconfirmed**.

## Who does what

| Role | What they get | What they cannot do |
| --- | --- | --- |
| Household (vendor) | Revenue book (StreetBiz orders counted automatically, direct sales added by hand), distance to the no-tax threshold, forecast for the year, to-do list and next deadline, tax estimate, confirmation that a notice was filed, Excel/PDF exports. | File anything with the tax office. |
| Ward officer | Counts of their households by status, tax code, notice and revenue group; the household list; cases worth passing to the tax office; announcements to households; Excel hand-over. | See a household's revenue unless it agreed; see a full tax code; collect tax. |
| Platform admin | Publish versioned tax policies (thresholds, rates, notice date). | Edit a published version, or see household data through this feature. |

## Data model (`db/StreetBiz_SQL_Server.sql`, section 12)

| Table | Purpose |
| --- | --- |
| `TaxPolicies` | One row per published *version* of a tax year's rules. The highest `version_no` of a year is in force; a year with no policy inherits the latest earlier one. Rows are never edited. `creator_role` is a computed column pinned to `PLATFORM_ADMIN`. |
| `TaxIndustryRates` | VAT and PIT rate per industry code for a policy. `FOOD_SERVICE` and `OTHER` are mandatory. |
| `VendorTaxProfiles` | Tax code (10, 12, 13 or 14 digits), industry, e-invoice flag, and `share_revenue_with_ward` (default **off**) with `consent_updated_at`. |
| `RevenueEntries` | Direct sales the household records (`CASH`, `BANK_TRANSFER`, `EWALLET`, `OTHER`). A wrong entry is **voided** (`voided_at`, `void_reason`), never deleted. |
| `TaxDeclarations` | The household's confirmation that it filed a period (`YEAR`/0 or `QUARTER`/1-4): amount declared, amount StreetBiz had at that moment, filing date, receipt reference, optional receipt file. Unique per vendor, year and period. |
| `TaxReferrals` | Cases the ward may pass to the tax office. Unique on `(reason_code, source_type, source_id)` so a source event is never referred twice. |
| `TaxAnnouncements` | Messages the ward sent, with segment `ALL`, `NOT_FILED` or `NO_TAX_CODE`. |

Reminders reuse the existing `Notifications` table (`notification_type = 'TAX'`).

## What counts as revenue

For a household and a Vietnamese calendar day (UTC+7, `BusinessCalendar`):

* **Online**: every `COMPLETED` order of the household's storefronts, by `completed_at`, **less
  refunds with status `SUCCESS`**. A pending or failed refund does not reduce revenue.
* **Recorded**: every `RevenueEntries` row that is not voided.

Total = online + recorded. Orders that are cancelled, or completed but outside the year, are not
counted. Money is summed in memory (not in SQL) so the same code is tested on SQLite.

## Rules (`Features/Tax/TaxRules.cs`, all pure functions)

* **Groups.** `G1` revenue ≤ threshold; `G2` ≤ group-2 ceiling; `G3` ≤ group-3 ceiling; `G4` above.
  Boundaries are inclusive of the lower group.
* **Threshold level.** `SAFE` below `warn_ratio × threshold`, `NEAR` from there up to the threshold,
  `OVER` above it.
* **Forecast.** Needs at least 14 days since the first sale day of the year. The pace is revenue to
  date divided by the days before today since that first sale (so a day still being recorded does not
  drag it down); the forecast is revenue to date plus that pace for each remaining day of the year,
  and the crossing date is when the pace carries revenue past the threshold (only if that is still
  within the year).
* **Estimate.** VAT on all revenue at the industry rate. PIT on revenue at the industry rate, on the
  part above the threshold when the policy says PIT deducts it. Optionally also the profit method
  (revenue minus an assumed cost ratio) at the group's profit rate; the cheaper method is flagged.
* **Calendar.** The yearly revenue notice is due on the policy's day of the **following** year. A
  quarter is due at the end of the month after the quarter. Status is `NOT_YET` (period not over),
  `OPEN`, `DUE_SOON` (14 days or fewer), `OVERDUE`, `FILED`, or `NOT_REQUIRED` (cumulative revenue
  through the quarter does not exceed the threshold).
* **Household status for the ward.** `ACTIVE` (a live contract), `SUSPENDED`, `ENDED`, or
  `NOT_STARTED` (approved but never rented).

## API

Household (`api/vendor/tax`, vendor only):

| Route | Purpose |
| --- | --- |
| `GET overview?year=` | Revenue, group, level, forecast, obligations, next deadline, profile, monthly bars. |
| `GET ledger?year=&month=` | One month day by day, with entries. |
| `POST revenue-entries` / `POST revenue-entries/{id}/void` | Add or void a direct sale. |
| `GET/PUT profile` | Tax code, industry, e-invoice, revenue sharing with the ward. |
| `GET estimate?year=&basis=&revenue=&costRatio=` | Reference tax. `basis` is `ACTUAL` or `PROJECTED`; giving `revenue` makes it `CUSTOM`. |
| `GET declarations?year=` | Yearly notice and four quarters with status. |
| `PUT/DELETE declarations/{year}/{periodType}/{periodNo}` | Confirm or retract "I filed this". |
| `GET revenue-book/export`, `GET summary/export` | `.xlsx` book and PDF yearly summary. |

Ward (`api/ward/tax`, ward officers of their own ward): `GET dashboard`, `GET households`,
`GET referrals?status=`, `POST referrals/refresh`, `POST referrals/{id}/report`,
`POST referrals/{id}/dismiss`, `GET/POST announcements`, `GET export`.

Platform (`api/platform/tax-policies`, platform admin): `GET` (all versions, newest first, with the
one in force marked) and `POST` (publish a new version).

## Privacy

* The ward **dashboard** returns counts only. Revenue groups are computed for every household so the
  distribution is meaningful, but no household is named.
* The ward **household list** shows `revenue` and `group` only for households with
  `share_revenue_with_ward = true`; for the others both are `null` and the UI says "Không chia sẻ".
  Changing the setting is audited (`TAX_REVENUE_SHARING_ON/OFF`).
* The tax code is always masked for the ward (`••••••••1234`). The ward always sees whether the yearly
  notice has been filed, because that is what it reminds households about.
* Referral summaries describe the source event, not the household's revenue.

## Referrals for the tax office

`RefreshTaxReferralsCommand` (and the daily sweep) look for these events in the ward's own records over
the last 90 days:

| Reason | Source |
| --- | --- |
| `UNREGISTERED_ACTIVITY` | A violation of type no-permit/unauthorised use with no contract, or a ward inspection scan whose QR is not in the system. |
| `INVALID_PERMIT_ACTIVITY` | An expired/invalid-permit or permit-tampering violation, or an inspection scan of a revoked/expired/suspended permit. |
| `ADDRESS_CHANGED` | An approved address change. |
| `BUSINESS_ENDED` | A household's last contract expired, was cancelled or revoked, with no other live contract. |
| `NEW_HOUSEHOLD` | A registration approved recently for a household with no tax code. Closes itself (`DISMISSED`, no handler) once the household gives a tax code. |

The refresh is idempotent. An officer then marks each case **reported** (with the letter's reference)
or **dismissed** (with a reason). Both are audited.

## Daily sweep (`TaxSweepHostedService`)

Runs every `TaxSweep:IntervalMinutes` (default 360). One run:

1. refreshes referrals for every ward;
2. sends threshold reminders (near the threshold; over it; forecast to cross). Each kind is sent at
   most once per household and year, because every notification is keyed on
   `(user, 'TAX', related type, related id)` and the repository sends a key only once;
3. sends deadline reminders for the previous and the current tax year: from 14 days before the due
   date, again at 3 days, and once overdue (until 30 days after), each stage once. Nothing is sent
   before a period has ended.

A household that already confirmed a period gets no reminder for it, and a quarter is only chased
for households whose cumulative revenue through the quarter exceeds the threshold.

## Operating it

1. **Seed** (`db/StreetBiz_Demo_Seed.sql`, section 14): two 2026 policies (500 million, superseded by
   1 billion), industry rates, tax profiles, and generated revenue entries relative to *today* so the
   demo always shows a believable year.
2. **Change the rules**: sign in as a platform admin and open *Chính sách thuế*. Publish a new version
   of the year; do not edit rows in the database.
3. **Verify** against the official texts before relying on any figure in front of real households.

## Tests

* `StreetBiz.Application.Tests`: `TaxRulesTests`, `TaxHandlerTests` (rules and handlers, privacy,
  validation, sweep behaviour).
* `StreetBiz.Infrastructure.Tests`: `TaxRepositoryTests` (SQLite: Vietnamese day boundaries, refunds,
  voided entries, ward scoping, referral generation and idempotency, announcements, once-only
  notifications) and `TaxDocumentRendererTests` (workbook sheets and formulas, PDF, privacy of the ward
  workbook).
* `StreetBiz.API.Tests`: every tax route appears in the endpoint contract test.
* Frontend: `tests/features/tax-api.test.ts`, `tests/features/tax-screens.test.tsx`.
