# Duplicate-registration check (AIC-03) and report summary (AIC-08)

Both are advisory (BR-41): they inform the ward officer and never reject, hold or change anything.
Neither calls an AI provider, so neither costs credits and neither needs a key; every sentence and
flag is a fixed rule over rows the ward already owns.

| | Route | Who | Where in the app |
|---|---|---|---|
| AIC-03 | `GET /api/ward/integrity/registrations/{registrationId}` | `WARD_AUTHORITY`, own ward only (404 otherwise) | Registration review screen, under the AI document check |
| AIC-08 | `GET /api/ward/reports/summary?from=&to=` | `WARD_AUTHORITY` | Collection report screen, above the violation ledger |

## AIC-03: what is flagged

A vendor with several storefronts legitimately files several registrations under one CCCD (BR-06),
so every rule compares against **other vendors'** registrations only.

| Code | Severity | Fires when |
|---|---|---|
| `ID_SHARED_ACROSS_VENDORS` | HIGH (MEDIUM if every match is rejected, withdrawn or a draft) | Same CCCD on a registration of another account, in any ward |
| `ADDRESS_SHARED_ACROSS_VENDORS` | MEDIUM | Same declared address (compared without case, accents or punctuation) on a submitted, under-review, more-information or approved registration of another account in the ward |
| `SLOT_CONTESTED_BY_SAME_ID` | HIGH | Another account with the same CCCD has an open or approved application for a slot this registration has an open application for |
| `MANY_OPEN_APPLICATIONS` | MEDIUM | 3 or more open rental applications on this registration |
| `REGISTRATION_BURST` | LOW | The account created 3 or more registrations within 30 days either side of this one |

Thresholds live in `RegistrationIntegrityRules`. A match in another ward is reported by ward and
status only; the other household's name and id are not shown to this officer.

If the registration has no CCCD number yet, the answer says so (`hasIdNumber: false`) rather than
reporting a clean check.

## AIC-08: what is said

The summary compares the period with the equally long period just before it. Every figure comes from
the WARD-14/15 rows (`ReportSummaryBuilder`), plus `GetPeriodFactsAsync` for the violation
breakdown, the on-time share of instalments paid in the period, and the largest overdue debtor.

| Topic | Tone rules |
|---|---|
| Collection | up 5% or more: positive; down 5% or more: attention; otherwise neutral; nothing collected after a collecting period: attention |
| Timeliness | on-time share of instalments paid in the period: 90% or more positive, 70% or more neutral, below that attention |
| Debt | any overdue instalment or unpaid penalty: attention (names the largest debtor) |
| Violations | none: positive; any without a standing penalty: attention |
| Occupancy | 80% or more positive, below 40% attention |
| Workload | nothing waiting: positive; 5 or more waiting: attention |

A period may span at most 366 days. "On time" is judged on the Vietnamese calendar day, the same
rule the collection report uses for its period boundaries.
