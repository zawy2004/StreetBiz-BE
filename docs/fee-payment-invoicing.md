# Fee, payment, invoice and reporting workflows

This document covers SYS-03–06, FEE-01–05 and WARD-14/15. The implementation
uses the existing `FeeSchedules`, `FeeScheduleItems`, `Invoices`,
`PaymentTransactions`, `PaymentCallbackEvents`, `Penalties` and `Notifications`
tables. No schema change was needed.

## Use-case coverage

| Use case | Backend behavior |
| --- | --- |
| SYS-03 | Generate a contract's fee schedule from `RentalContractApprovedEvent` (published by WARD-08/09, not yet implemented — a Development-only endpoint stands in). Splits the term into 30-day instalments; PER_TERM zone components (admin fee, deposit) fall entirely on the first instalment; the last instalment absorbs rounding. Regenerating supersedes the current revision instead of editing it, and is refused once any instalment is PAID or when the contract is no longer `ACTIVE`. |
| SYS-04 | One callback endpoint (`POST /api/payments/{provider}/callback`) for every payment purpose. Looks up which purpose (`RENTAL_FEE`/`PENALTY`/`ORDER`) a transaction belongs to before applying, so a real MoMo/ZaloPay merchant account's single configured webhook URL still works. Idempotent: a repeated callback for an already-terminal transaction is recorded as `DUPLICATE`, never re-applied. |
| SYS-05 | An invoice (`HD-{year}-{6 digits}`) is issued in the same transaction as a successful callback — never before payment is confirmed (BR-32). |
| SYS-06 / FEE-02 | `FeeReminderHostedService` sweeps hourly (configurable): instalments past their due date move `PENDING → OVERDUE` with a notification; instalments due within 3 days get a reminder, at most once per calendar day. |
| FEE-01 | Vendor opens a checkout for one fee instalment; refused if it is not `PENDING`/`OVERDUE` or belongs to a superseded revision. Retrying with the same `Idempotency-Key` replays the original transaction; the key reused for a different instalment or provider is `409`. |
| FEE-03 | Vendor lists and reads their own invoices (fee or penalty). |
| FEE-04 | Vendor opens a checkout for one penalty; refused unless `UNPAID`. Same idempotent replay as FEE-01. |
| FEE-05 | Vendor's payment-attempt history (fee and penalty purposes) and their own violations, most recent first. |
| — | FinanceHome support: `GET /summary` (outstanding totals, overdue count, next due date), `GET /fees?status=` and `GET /penalties?status=` (cross-contract lists behind the two tabs). |
| WARD-14 | Ward-scoped fee/penalty collection totals for a period (default: the 1st of the current month through today), plus the ward's 10 most recent violations. |
| WARD-15 | Ward-scoped operational snapshot: slot occupancy, pending registrations/applications, active contracts, revenue collected, outstanding debt, open violations. |

## State model

```text
FeeScheduleItems.item_status:
  PENDING --payment success--------> PAID
  PENDING --due date passes--------> OVERDUE
  OVERDUE --payment success--------> PAID

Penalties.penalty_status:
  UNPAID --payment success---------> PAID
  UNPAID --ward waives-------------> WAIVED   (not exposed by an endpoint yet)

PaymentTransactions.transaction_status:
  PENDING --checkout opened--------> PENDING
  PENDING --callback SUCCESS-------> SUCCESS  (terminal)
  PENDING --callback FAILED--------> FAILED   (terminal)
```

A transaction already `SUCCESS`/`FAILED` is terminal: a repeated callback for it
is recorded as `DUPLICATE` and changes nothing. `PaymentCallbackEvents` keeps
every callback received — matched or not, valid signature or not — as the
evidence trail for a provider dispute.

## HTTP API

| Use case | Endpoint |
| --- | --- |
| — | `GET /api/vendor/finance/summary` — outstanding totals, overdue count, next due date |
| — | `GET /api/vendor/finance/fees?status=` — cross-contract fee instalment list |
| FEE-01 | `POST /api/vendor/finance/fees/{feeItemId}/checkout` (header `Idempotency-Key`, body `{ "provider": "MOMO" \| "ZALOPAY" }`) |
| — | `GET /api/vendor/finance/penalties?status=` — cross-violation penalty list |
| FEE-04 | `POST /api/vendor/finance/penalties/{penaltyId}/checkout` (same shape) |
| — | `POST /api/vendor/finance/payments/{transactionId}/sandbox-confirm` — Development + `Payments:SandboxEnabled` only, same role as `POST /api/orders/{orderId}/payment/sandbox-fail` |
| FEE-05 | `GET /api/vendor/finance/payments`, `GET /api/vendor/finance/violations` |
| FEE-03 | `GET /api/vendor/finance/invoices`, `GET /api/vendor/finance/invoices/{invoiceId}` |
| SYS-04 | `POST /api/payments/{provider}/callback` (shared with Commerce; see "Payment boundary") |
| SYS-03 | `GET`/`POST /api/dev/contracts/{contractId}/fee-schedule` — Development-only stand-in for WARD-08/09 |
| — | `POST /api/dev/finance/reminders/sweep?today=` — Development-only manual trigger for the reminder sweep, optionally replaying a specific day |
| WARD-14 | `GET /api/ward/reports/collection?from=&to=` |
| WARD-15 | `GET /api/ward/dashboard` |

## Payment boundary

`PaymentTransactions.payment_purpose` (`RENTAL_FEE`/`PENALTY`/`ORDER`) and its
matching CHECK constraint (`CK_PaymentTransactions_PurposeMatchesTarget`) were
already in the schema before this module — designed from the start for one
shared payment ledger across sidewalk fees, penalties and Phase 2 orders.

`ProcessPaymentCallbackCommandHandler` (in `Features/Commerce`, shared with
Orders) verifies the provider signature first, then calls
`IFinanceRepository.FindPaymentPurposeAsync` — a read-only lookup by the same
provider reference / idempotency key the write path will match on — to decide
which repository's `ApplyPaymentCallbackAsync` applies the callback. Exactly
one repository runs per callback, so exactly one `PaymentCallbackEvents` row is
written regardless of purpose. `ICommerceRepository` was not changed; only
`PaymentGatewayCheckoutRequest`'s `OrderId`/`OrderCode` fields were renamed to
`ReferenceId`/`ReferenceCode` so the same gateway interface serves all three
purposes (a positional-record change with no call-site edits required, since
`OrderId`/`OrderCode` already occupied those parameter positions).

`IPaymentGateway`/`ConfiguredPaymentGateway` (HMAC-SHA256 verification, sandbox
checkout URLs) is unchanged from Commerce. Financial checkouts use the same
Development-only sandbox pattern as Orders (`Payments:SandboxEnabled`) rather
than faking a signed callback.

## SYS-03 sequencing (important while WARD-08/09 do not exist)

`RentalContractApprovedEvent` (`Application/Common/Events`) is a MediatR
notification, not a command: whoever approves a contract or a renewal
publishes it, and `GenerateFeeScheduleOnContractApproved` reacts by generating
the schedule. This keeps the ward-approval workflow unaware the finance module
exists. The publisher is expected to raise the event inside the same
transaction as the approval write, so the contract and its first schedule
commit together. Until WARD-08/09 exist, `POST /api/dev/contracts/{id}/fee-schedule`
publishes the event manually for testing.

## Business calendar

Due dates, "today" and report periods are Vietnamese calendar days
(`BusinessCalendar`, UTC+7), while every timestamp is stored in UTC. The sweep's
"today", the ward report's default period and its `[from, to]` boundaries, and
the invoice number's year all use the Đà Nẵng day — a UTC day would roll over at
07:00 local time and file a payment made at 03:00 on the 1st under the
previous month.

## Superseded revisions

Regenerating a schedule supersedes the current revision but leaves its items
`PENDING`/`OVERDUE` (the CHECK constraint has no "superseded" status). Every
query about money still owed — the vendor's lists and summary, checkout, the
callback's "still payable" check, the reminder sweep and the ward's pending,
overdue and outstanding totals — therefore filters to
`fee_schedule.superseded_at IS NULL`. Collected revenue is never filtered that
way: money received stays revenue.

## Double payment

A vendor can open two checkouts on the same instalment (two tabs, a retry
with a new key). Only the first to be confirmed applies: both the real
callback and the Development sandbox-confirm re-check that the target is
still payable inside their Serializable transaction, so the second never marks
it paid again or issues a second invoice. The real callback records it as
`REJECTED`; sandbox-confirm answers `422`.

## Reminder sweep idempotency

The schema has no "reminder already sent" flag, and adding one needs a
reviewed migration (see `docs/migration-guide.md`). Instead, a reminder is
skipped if a `Notifications` row already exists for that fee item with
`notification_type = 'FEE'` and `sent_at` since the start of the same
Vietnamese day (one query for all candidates, not one per item). This only
holds together when the sweep's `today` parameter and the wall clock used to
stamp `sent_at` come from the same instant — true for both the hourly hosted
service and the no-argument dev endpoint call, but **not** when the dev
endpoint's `today` query parameter is used to replay a different day: the
notification it writes is still stamped with the real current time, so
replaying the same overridden day twice will send two reminders. This is a
testing-only caveat, not a production behavior.

## Remaining work

- **Integration with WARD-08/09 on `develop`.** Their approval
  (`WardComplianceService`) writes a `FeeSchedules` header with
  `total_amount = term × price_per_day` and **no `FeeScheduleItems`**, and
  renewal supersedes the current revision even when it has been paid into.
  After merging `develop`, approval should publish `RentalContractApprovedEvent`
  inside its transaction (`ReplaceFeeScheduleAsync` joins an ambient
  transaction for exactly this), and renewal needs an "extend the current
  schedule" operation rather than a regeneration — whether the deposit /
  admin fee is charged again on renewal is a business decision BR-18 does not
  make.
- Checkout should also call `IPaymentGateway.IsProviderAvailable` (added on
  `develop` for Commerce) before opening a transaction.
- No PDF rendering for `FEE-03`'s "download invoice" — the endpoint returns
  JSON only.

### Recommended schema follow-ups (need a reviewed migration)

| Change | Why |
| --- | --- |
| Filtered `UNIQUE` on `Invoices(fee_item_id)` and `Invoices(penalty_id)` `WHERE … IS NOT NULL` | Makes "one invoice per paid obligation" a database guarantee, not only an application check. |
| Index `PaymentTransactions(provider_reference)` | Every provider callback looks the transaction up by it; today that is a table scan. |
| Index `FeeScheduleItems(fee_schedule_id)` | Foreign key with no index; every schedule/ordinal read joins on it. |
| Index `Invoices(vendor_id, issued_at)` | FEE-03's list filters and sorts on exactly this. |
| Index `Notifications(related_entity_type, related_entity_id, sent_at)` | The reminder sweep's "already reminded today" check. |

## Verification

```powershell
dotnet build StreetBiz.Backend.sln
dotnet test StreetBiz.Backend.sln
```

Manual smoke test (Development, `Payments:SandboxEnabled=true`): sign in as
the seeded vendor (`0905000101` / `Password123!`), `POST .../fees/{id}/checkout`
on a `PENDING`/`OVERDUE` instalment, confirm via sandbox-confirm, then
`GET .../invoices` to see it issued. Sign in as the seeded ward officer
(`0983000001` / `Password123!`) for `GET /api/ward/dashboard` and
`GET /api/ward/reports/collection`. See `docs/dev-test-accounts.md` for the
full seeded scenario these numbers are checked against.
