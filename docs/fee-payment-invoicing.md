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
| FEE-03 | Vendor lists and reads their own invoices (fee or penalty) and downloads each as a PDF receipt. |
| FEE-04 | Vendor opens a checkout for one penalty; refused unless `UNPAID`. Same idempotent replay as FEE-01. |
| FEE-05 | Vendor's payment-attempt history (fee and penalty purposes) and their own violations, most recent first. |
| — | FinanceHome support: `GET /summary` (outstanding totals, overdue count, next due date), `GET /fees?status=` and `GET /penalties?status=` (cross-contract lists behind the two tabs). |
| WARD-14 | Ward-scoped fee/penalty collection totals for a period (default: the 1st of the current month through today), plus the ward's 10 most recent violations; exportable as an Excel workbook listing every receipt behind the totals. |
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
| — | `POST /api/vendor/finance/payments/{transactionId}/sandbox-confirm` — Development + `Payments:SandboxEnabled` only, same role as `POST /api/orders/{orderId}/payment/sandbox-fail`; refused for a MoMo transaction once MoMo keys are configured |
| — | `POST /api/vendor/finance/payments/{transactionId}/sync` — asks MoMo for the transaction's real state and applies it (the return page calls it, since MoMo's IPN cannot reach localhost) |
| FEE-05 | `GET /api/vendor/finance/payments`, `GET /api/vendor/finance/violations` |
| FEE-03 | `GET /api/vendor/finance/invoices`, `GET /api/vendor/finance/invoices/{invoiceId}` |
| FEE-03 | `GET /api/vendor/finance/invoices/{invoiceId}/pdf` — the payment receipt as an A5 PDF (`{invoiceNumber}.pdf`) |
| SYS-04 | `POST /api/payments/{provider}/callback` (shared with Commerce; see "Payment boundary") |
| SYS-03 | `GET`/`POST /api/dev/contracts/{contractId}/fee-schedule` — Development-only stand-in for WARD-08/09 |
| — | `POST /api/dev/finance/reminders/sweep?today=` — Development-only manual trigger for the reminder sweep, optionally replaying a specific day |
| WARD-14 | `GET /api/ward/reports/collection?from=&to=` |
| WARD-14 | `GET /api/ward/reports/collection/export?from=&to=` — the same report as `.xlsx`: summary (with the on-time rate), every receipt issued in the period, debtors, zones, recent violations |
| WARD-15 | `GET /api/ward/dashboard` |
| FEE-01/03 | `GET /api/vendor/finance/contracts` — every contract with a fee schedule and how far it is paid |
| FEE-01/03 | `GET /api/vendor/finance/contracts/{contractId}/schedule` — one schedule, instalment by instalment, with invoices |
| FEE-01 | `GET /api/vendor/finance/fees/{feeItemId}` — one instalment and its contract (payment screen and its receipt) |
| FEE-03/05 | `GET /api/vendor/finance/statement?year=` — a year's receipts and instalments as `.xlsx` |
| WARD-14 | `GET /api/ward/reports/debtors` — households with overdue fees, most overdue first |
| WARD-14 | `POST /api/ward/reports/debtors/{contractId}/remind` — in-app reminder, once per contract per day (`409` after) |
| WARD-14 | `GET /api/ward/reports/performance?from=&to=` — on-time rate for fees falling due in the period, figures by zone |
| WARD-14 | `GET /api/ward/reports/trend?months=6` — fees and penalties collected month by month |
| WARD-14 | `GET /api/ward/reports/receivables` — overdue fees by age (1–30, 31–60, over 60 days), fees due in the next 30 days, payments owed back |
| WARD-14 | `GET /api/ward/reports/refunds` — payments received twice (`REFUND_REQUIRED` first, then `REFUNDED`); `POST /api/ward/reports/refunds/{transactionId}/mark-refunded` records the repayment |

## Vendor fee schedules (FEE-01/03/05)

`FeeScheduleProgress` (Application, pure) turns a contract's current instalments into what the
vendor screens show: paid/outstanding amounts and counts, the next instalment, and per instalment
`daysOverdue` or `daysUntilDue`. An unpaid instalment whose due date has passed reads as `OVERDUE`
even before the hourly sweep flips its status, so the screens never show a late fee as on time.
Instalments are numbered within their own schedule, as in the fee list, and carry the invoice they
produced once paid. The yearly statement files receipts by the Vietnamese day they were issued and
instalments by due date.

## Ward collections (WARD-14)

`WardCollectionAnalytics` (Application, pure) computes every figure from small ward-scoped row
sets read by `WardCollectionRepository`:

- **Debtors**: contracts with at least one unpaid instalment past its due date, most overdue first,
  with the overdue and upcoming amounts and the last reminder.
- **On-time rate**: of the current instalments falling due in the period, the share paid by the end
  of the due date in Vietnam time (an instalment paid at 23:30 on its due date in Đà Nẵng is on
  time; 00:30 the next day is late). `null` when nothing fell due, never a misleading 0%.
- **By zone**: rented slots (a slot under an active contract), fees collected in the period and
  what is still owed. Revenue includes superseded revisions (money received stays revenue);
  "due" and "owed" count current revisions only.
- **Trend**: the last N calendar months, payments filed under the month they were made in Vietnam.
- **Reminders**: one per contract per Vietnamese day, only for overdue debt; each writes the
  vendor's `FEE` notification (`related_entity_type = RentalContract`) and an `AuditLogs` row
  `FEE_DEBT_REMINDER_SENT`, since it is a ward officer acting on a household.

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

Fee and penalty checkouts go through Commerce's `IPaymentGateway` unchanged,
passing `Purpose = RENTAL_FEE`/`PENALTY` so MoMo sends the vendor back to
`FeeReturnUrl`/`PenaltyReturnUrl` instead of the order page. Checkout first
asks `IsProviderAvailable`, so a provider with neither keys nor the sandbox is a
`422` before any transaction is written.

## MoMo (All-In-One v2)

With the `Payments:Momo` keys below, `MOMO` checkouts call MoMo's
`/create` and return its real payment page; without them they fall back to
the Development sandbox (`Payments:SandboxEnabled`), exactly as before.

- **One MoMo `orderId` per attempt.** MoMo refuses an `orderId` it has seen
  before (`resultCode` 41), even with a new `requestId`, and the shared test
  merchant has already seen the low `SB-T{n}` values. Each create call sends
  `SB-T{transactionId}-{8 hex}`; the transaction id stays readable for the
  return page.
- **The Idempotency-Key rides in `extraData`.** It is signed with the request
  and echoed back in the IPN and query response, so a callback for any attempt
  finds its transaction by idempotency key even after a retry replaced
  `provider_reference`.
- **Only the paid attempt's reference is kept.** A `SUCCESS` for an earlier
  attempt still applies and stores that attempt's `orderId` as
  `provider_reference`, so the receipt prints the reference the vendor
  actually paid. A `FAILED` for an earlier attempt (the vendor abandoned it and
  retried) is recorded as `REJECTED` and cannot fail the retry.
- **A refused create does not leave a ghost.** If MoMo refuses `/create`, the
  transaction (still `PENDING`, never opened) is marked `FAILED` and MoMo's
  message is shown to the vendor.
- **Sync instead of a public IPN locally.** MoMo cannot call
  `http://localhost`, so the return page calls `.../payments/{id}/sync`, which
  asks MoMo's `/query` and applies the answer through the same
  `ApplyPaymentCallbackAsync` path the IPN uses. A deployed server with a
  public `NotifyUrl` gets the IPN as well; both paths are idempotent.
- **No fake confirmation once MoMo is real.** `sandbox-confirm` refuses a MoMo
  transaction while MoMo keys are configured, so a demo cannot mark as paid
  something MoMo never received.

### Local setup

MoMo's public test merchant works for a student project; no business
registration is needed. Keys go in user-secrets, never in `appsettings.json`:

```powershell
$p = "src/StreetBiz.API"
dotnet user-secrets --project $p set "Payments:Momo:ApiBaseUrl" "https://test-payment.momo.vn/v2/gateway/api"
dotnet user-secrets --project $p set "Payments:Momo:PartnerCode" "<partner code>"
dotnet user-secrets --project $p set "Payments:Momo:AccessKey" "<access key>"
dotnet user-secrets --project $p set "Payments:Momo:SecretKey" "<secret key>"
dotnet user-secrets --project $p set "Payments:Momo:ReturnUrl" "http://localhost:5173/customer/orders/{referenceId}/payment"
dotnet user-secrets --project $p set "Payments:Momo:FeeReturnUrl" "http://localhost:5173/vendor/finance/fees/{referenceId}/payment"
dotnet user-secrets --project $p set "Payments:Momo:PenaltyReturnUrl" "http://localhost:5173/vendor/finance/penalties/{referenceId}/payment"
dotnet user-secrets --project $p set "Payments:Momo:NotifyUrl" "http://localhost:5023/api/payments/momo/callback"
```

`IsMomoConfigured` needs `ApiBaseUrl`, `PartnerCode`, `AccessKey`,
`SecretKey`, `ReturnUrl` and `NotifyUrl`. Remove the keys (or leave one empty)
to go back to the instant sandbox. On MoMo's test page, pay with the test ATM
card MoMo publishes on developers.momo.vn.

## Fee schedule lifecycle (SYS-03)

What is billed is the **rent only**, `price_per_day × days`: the "tiền thuê tính vào hợp đồng"
line of the SIDE-02 quote. The zone's other components (cleaning, admin fee, deposit) are
reference-only there ("chưa tính vào hợp đồng"), so billing them would charge more than the vendor
agreed to. Instalments are 30-day periods due the day each starts, never sooner than 7 days after
the schedule was issued (`FeeInstalmentPlanner.GraceDays`), so a contract approved today does not
open with an instalment overdue tomorrow.

Three paths keep a schedule in step with its contract:

| Path | Trigger | What it does |
| --- | --- | --- |
| Approval | `RentalContractApprovedEvent` (published by WARD-08 inside its transaction) → `GenerateFeeScheduleOnContractApproved` | Generates the schedule. |
| Renewal | `RentalContractRenewedEvent` (WARD-09) → `ExtendFeeScheduleOnContractRenewed` → `ExtendFeeScheduleCommand` | **Appends** the renewal's days as new instalments to the open schedule and raises its total. Instalments of the current term, paid or not, are untouched; publishing it twice is a `409`. |
| Reconciliation | The hourly sweep, first step (`ReconcileFeeSchedulesCommand`) | Safety net for whichever workflow changed the contract without raising an event: bills an `ACTIVE` contract in force that has no instalments and nothing paid (the approving officer is the actor), and closes (`CANCELLED`) the open instalments of a contract that ended early whose period never began. |

An instalment is "of a period that never began" when it is due after the last day the contract was in
force and is not the first one (whose due date may sit a grace period past its start). The last day
is the Vietnam date of `cancelled_at` (a returned slot) or of the permit's `revoked_at` (a revoked
permit). The vendor is told once, with the amount. A suspended contract keeps billing: suspension is
not an end, and whether to pause billing is the ward's call.

The ward's approval and renewal on `develop` (`WardComplianceService`) do not publish these events
yet; see "Remaining work". Until they do, reconciliation bills an approved contract within an hour,
and `POST /api/dev/contracts/{id}/fee-schedule` and `.../fee-schedule/extend` (Development only)
publish the events by hand.

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

## Payment order, double payment and refunds

**Instalments are paid oldest first.** Checkout for an instalment is refused (`422`, "Vui lòng
thanh toán kỳ N (hạn …) trước") while an earlier one of the same schedule is unpaid, so an overdue
instalment is never skipped. The schedule read marks the oldest unpaid one `payableNow`.

**One open payment per instalment/penalty.** Opening a checkout for one that already has a PENDING
payment hands that payment back when the provider is the same (the gateway then opens a fresh page
for it), and closes it (`FAILED`) first when the provider differs. Two tabs or MoMo-then-ZaloPay can
no longer leave two payments racing.

**A payment is never dropped.** A provider's success is applied even for a payment StreetBiz had
closed as `FAILED` (its page was still open). If the instalment/penalty is no longer payable — another
payment settled it, or it was closed — the money is **owed back**: the payment becomes
`REFUND_REQUIRED`, no invoice is issued, the vendor is notified and an audit entry
(`FINANCE_PAYMENT_REFUND_REQUIRED`) keeps the provider's reference. The ward lists these
(`GET /api/ward/reports/refunds`) and records the repayment with
`POST /api/ward/reports/refunds/{transactionId}/mark-refunded` (`REFUNDED`, audited, vendor
notified). StreetBiz cannot send money itself, so this records a refund made through the provider's
merchant portal or in cash. A repeated report for a payment already `SUCCESS`, `REFUND_REQUIRED` or
`REFUNDED` is `DUPLICATE`.

**Stale payments.** The sweep's third step settles payments left PENDING for 30 minutes
(`StalePaymentPolicy`): MoMo is asked for its state and a reported result is applied like a callback;
a payment with no provider page (sandbox, or the page was never opened) is closed. A MoMo order still
reported in flight is closed after 24 hours. Closing is safe for the reason above.

## Sweep steps

`FeeReminderHostedService` runs three independent steps each tick, in this order (a failing step is
logged and does not stop the others): reconcile schedules, overdue/reminder sweep, settle stale
payments. Reconciling first means no "sắp đến hạn" reminder goes out for an instalment about to be
closed; the reminder step also skips contracts that are not `ACTIVE`/`SUSPENDED`. The Development
endpoints `POST /api/dev/finance/schedules/reconcile` and `.../payments/settle-stale` run the
other two on demand.

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

- **Integration with WARD-08/09 on `develop`** (code owned by the ward workflow, not changed
  here). Approval (`WardComplianceService`, ~line 597) writes a `FeeSchedules` header with
  `total_amount = term × price_per_day` and **no `FeeScheduleItems`**; renewal (~line 1016)
  supersedes the current revision even when it has been paid into, which drops the instalments of the
  current term that are not due yet. Both should instead publish `RentalContractApprovedEvent` /
  `RentalContractRenewedEvent` inside their transaction (`ReplaceFeeScheduleAsync` and
  `AppendInstalmentsAsync` join an ambient transaction for exactly this) and drop their own
  `FeeSchedules` write. Until then reconciliation bills approved contracts, but a renewal still loses
  the not-yet-due instalments of the current term.
- **Deposits** are not billed (see above), so there is nothing to refund or settle when a contract
  ends. Charging a deposit means a separate obligation with its own life cycle (held, refunded,
  deducted), not an instalment.
- **Receipt, not a tax e-invoice.** The PDF ("Biên lai thanh toán") is an internal
  record of a confirmed payment. It is not the official fee or penalty receipt
  ("biên lai thu phí, lệ phí" / "biên lai thu tiền phạt") that the authority
  issues under Decree 123/2020 (no registered template or digital signature),
  and its footer says so. Issuing official e-receipts needs an e-invoice
  provider integration.

### Recommended schema follow-ups (need a reviewed migration)

Done: the unique invoice indexes (`UX_Invoices_FeeItem`, `UX_Invoices_Penalty`) and the
`CANCELLED` / `REFUND_REQUIRED` / `REFUNDED` statuses are in `db/StreetBiz_SQL_Server.sql`
(change log 2026-10-09); rebuild the local database to get them.

| Change | Why |
| --- | --- |
| Index `PaymentTransactions(provider_reference)` | Every provider callback looks the transaction up by it; today that is a table scan. |
| Index `FeeScheduleItems(fee_schedule_id)` | Foreign key with no index; every schedule/ordinal read joins on it. |
| Index `Invoices(vendor_id, issued_at)` | FEE-03's list filters and sorts on exactly this. |
| Index `Notifications(related_entity_type, related_entity_id, sent_at)` | The reminder sweep's "already reminded today" check. |

## Documents

`IFinanceDocumentRenderer` (Infrastructure `Documents/FinanceDocumentRenderer`)
renders both files:

- **Receipt PDF** (QuestPDF, A5): ward name, receipt number, payer, business,
  slot, what was paid (instalment period, or violation and sanction decision
  number), provider, the paid attempt's MoMo reference, paid time in Vietnam
  time, the amount in figures and in words (`VietnameseMoneyWords`). Only the
  invoice's own vendor can download it; another vendor's invoice is `404`.
- **Collection report XLSX** (ClosedXML): sheet "Tổng hợp" (the WARD-14
  totals), "Bảng kê thu" (every receipt issued in the period, with a `SUM`
  total row), "Vi phạm gần đây". Only ward accounts can export, scoped to their
  own ward.

The Be Vietnam Pro font (SIL OFL, licence file alongside) is embedded in the
assembly, so Vietnamese renders the same on any server with no system fonts
installed. QuestPDF runs under its Community licence.

## Verification

```powershell
dotnet build StreetBiz.Backend.sln
dotnet test StreetBiz.Backend.sln
```

Manual smoke test (Development, a database built by
`scripts/setup-local-db.ps1`): sign in as the seeded vendor (`0905000101` /
`Password123!`), `POST .../fees/{id}/checkout` on a `PENDING`/`OVERDUE`
instalment, pay (MoMo test page, or sandbox-confirm without MoMo keys), then
`GET .../invoices` to see it issued and `GET .../invoices/{id}/pdf` for the
receipt. Sign in as the seeded ward officer (`0983000001` / `Password123!`)
for `GET /api/ward/dashboard`, `GET /api/ward/reports/collection` and
`GET /api/ward/reports/collection/export`. `docs/database.md` lists every
seeded account.
