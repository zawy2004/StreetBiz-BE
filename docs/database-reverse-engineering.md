# Database reverse-engineering

## Inspection result

StreetBizDB was inspected read-only before scaffolding:

- SQL Server version: 16.0.1190.2, Developer Edition (SQL Server 2022)
- User tables: 46
- Tables with primary keys: 46
- Tables without primary keys: 0
- Foreign keys: 93
- Unique constraints: 21
- Unique indexes excluding primary-key indexes: 192 (including indexes that
  back unique constraints)
- Non-unique indexes: 44
- __EFMigrationsHistory: absent
- Database views scaffolded as keyless types: 2

No database data, schema, constraints, indexes, or migration history were changed.

## Tables

~~~text
AddressChangeRequests
AdministrativeUnits
AIAssistanceLogs
AuditLogs
BusinessRegistrations
Complaints
DigitalPermits
FeeScheduleItems
FeeSchedules
FoodCategories
Invoices
MenuItems
Notifications
OrderItems
Orders
OrderStatusHistory
OtpChallenges
PaymentCallbackEvents
PaymentTransactions
Penalties
PenaltyFeeSchedules
PermitScanLogs
PricingZones
RefundTransactions
RegistrationEvidence
RenewalRequests
RentalApplications
RentalContracts
ReportedContent
ReportExports
Reviews
Roles
ShoppingCartItems
ShoppingCarts
SidewalkSlots
SlotTransferRequests
StorefrontBusinessHours
Storefronts
UserAccounts
UserDevices
UserSessions
VendorComments
VendorReports
Vendors
Violations
ViolationTypes
~~~

The views vw_PermitValidity and vw_VendorRatings generated the keyless model
types vw_PermitValidity and vw_VendorRating. Keyless view types are expected and
are not missing-table-primary-key defects.

## Scaffold command

Run from the solution root after setting STREETBIZ_DB_CONNECTION:

~~~powershell
dotnet ef dbcontext scaffold "$env:STREETBIZ_DB_CONNECTION" Microsoft.EntityFrameworkCore.SqlServer --project src/StreetBiz.Infrastructure --startup-project src/StreetBiz.API --context StreetBizDbContext --context-dir Persistence --output-dir Persistence/ScaffoldedModels --no-onconfiguring --use-database-names --force
~~~

The --no-onconfiguring flag is mandatory so credentials are not generated in
StreetBizDbContext. The --force flag overwrites generated files; inspect Git
changes and back up any accidental edits before re-running it.

## Generated output

- DbContext:
  src/StreetBiz.Infrastructure/Persistence/StreetBizDbContext.cs
- Models:
  src/StreetBiz.Infrastructure/Persistence/ScaffoldedModels
- Result:
  46 table models and 2 keyless view models

## Extending generated types

Do not edit scaffolded files when an extension can be expressed separately.
Generated classes are partial. For example:

~~~csharp
namespace StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

public partial class Vendor
{
    // Persistence-side helper only. Domain behavior belongs in Domain.
}
~~~

Keep the extension outside ScaffoldedModels if it should survive a complete
replacement of that directory.

## Known limitation: filtered unique indexes read as one-to-one

The scaffolder cannot see a `WHERE` clause on a `CREATE UNIQUE INDEX`. If that
index sits on a single foreign-key column, it generates a one-to-one
relationship (`HasOne(...).WithOne(...)`) as if the FK value were globally
unique — even though the filter only means "unique among *current/open* rows"
and the table is meant to hold several rows per parent over time (an audit
trail of superseded/closed/revoked rows). At runtime this throws
`InvalidOperationException: Unable to set 'IsUnique' to 'False' ... because
the navigation has the opposite multiplicity` the first time the model is
built, once a repository tries to reconfigure it — build succeeds, tests can
pass, and it still crashes the first real request.

**How to tell a real 1:1 from this false positive:** find the index in
`db/StreetBiz_SQL_Server.sql`. `CONSTRAINT ... UNIQUE (col)` or a
`CREATE UNIQUE INDEX` with **no** `WHERE` is a genuine 1:1 — leave it as
scaffolded (e.g. `Storefront.contract`/`Storefront.registration`, which use
plain `UQ_Storefronts_Contract`/`UQ_Storefronts_Registration`). A
`CREATE UNIQUE INDEX ... WHERE <condition>` on a single FK column is the false
positive — fix it, don't touch the SQL:

~~~csharp
// In Persistence/Configurations/ModelFixups.cs (OnModelCreatingPartial):

// (a) Unmap the scaffolded singular navigation on the "one" side — it was
// generated to pair with WithOne() below and can't coexist with WithMany().
modelBuilder.Entity<Parent>().Ignore(e => e.SingularChildNav);

// (b) Reconfigure as one-to-many, keeping the FK constraint name scaffold
// already gave it (find it next to the original .HasOne(...).WithOne(...)
// in StreetBizDbContext.cs before removing/replacing that block).
modelBuilder.Entity<Child>()
    .HasOne(d => d.parent)
    .WithMany()
    .HasForeignKey(d => d.parent_id)
    .OnDelete(DeleteBehavior.ClientSetNull)
    .HasConstraintName("FK_Child_Parent");
~~~

A repository that needs "the current row" then queries it directly (matching
the same condition as the index's `WHERE` clause) instead of going through
`parent.SingularChildNav`, which no longer exists.

Fixed so far (see `ModelFixups.cs` for the exact code): `DigitalPermits`
(`UQ_DigitalPermits_LivePerContract`, filters `permit_status <> 'REVOKED'`),
`RenewalRequests` (`UQ_RenewalRequests_OpenPerContract`, filters
`renewal_status IN (...)`), `AddressChangeRequests`
(`UQ_AddressChangeRequests_OpenPerRegistration`, same shape), `FeeSchedules`
(`UQ_FeeSchedules_CurrentPerContract`, filters `superseded_at IS NULL`).
`UQ_PenaltyFeeSchedules_CurrentRate` and `UQ_ShoppingCarts_ActivePerStorefront`
are also filtered but sit on a *composite* of two different FK columns, which
scaffold never turns into a 1:1 on either FK — already correctly `WithMany()`.

**Verifying the fix is not optional:** `dotnet build` never catches this —
the exception only fires when EF actually builds the model against a live
`DbContext`. After any fix (or after re-scaffolding), run the API against a
real database and call any endpoint that touches the affected `DbContext`;
either a benign `"<nav> was first mapped explicitly and then ignored"` warning
in the log (expected) or the `opposite multiplicity` exception (still broken)
tells you which it is.

## Re-scaffolding checklist

1. Verify database connectivity and permissions.
2. Query tables, primary keys, foreign keys, unique constraints, indexes, views,
   and __EFMigrationsHistory in read-only mode.
3. Stop if any table required by the application has no primary key.
4. Commit or preserve current generated files before using --force.
5. Run the scaffold command from the solution root.
6. Review the full generated diff, especially type/nullability and relationship
   changes.
7. Re-run `grep -n "CREATE UNIQUE INDEX" db/StreetBiz_SQL_Server.sql` and check
   every filtered one against the new `StreetBizDbContext.cs` — the scaffolder
   makes the same false-positive 1:1 mistake every time it runs, on any table
   that matches the pattern above, including new tables added since the last
   scaffold. Update `ModelFixups.cs` for anything newly affected.
8. Rebuild and rerun tests.
9. Never interpret re-scaffolding as approval to update the database.
