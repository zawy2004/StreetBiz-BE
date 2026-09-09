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

## Re-scaffolding checklist

1. Verify database connectivity and permissions.
2. Query tables, primary keys, foreign keys, unique constraints, indexes, views,
   and __EFMigrationsHistory in read-only mode.
3. Stop if any table required by the application has no primary key.
4. Commit or preserve current generated files before using --force.
5. Run the scaffold command from the solution root.
6. Review the full generated diff, especially type/nullability and relationship
   changes.
7. Rebuild and rerun tests.
8. Never interpret re-scaffolding as approval to update the database.
