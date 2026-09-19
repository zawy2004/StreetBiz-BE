# Community vendor workflows (BUY-01–BUY-05)

These endpoints use the existing StreetBizDB schema and do not add a migration.
Only vendors with an active account, an approved registration and a currently
`VALID` permit are exposed by the public map and profile queries.

| Use case | Method and route | Authentication | Behaviour |
| --- | --- | --- | --- |
| BUY-01 | `GET /api/community/vendors` | Anonymous | Lists active permitted vendor locations. Optional `latitude`, `longitude` and `radiusMeters` must be supplied together. |
| BUY-02 | `POST /api/community/permits/verify` | Anonymous, rate limited | Verifies the signed QR payload against live permit status and records a `PUBLIC_CHECK` scan log. |
| BUY-03 | `GET /api/community/vendors/{vendorId}` | Anonymous | Returns public identity, permit, slot, ratings and the newest 50 community comments. |
| BUY-04 | `PUT /api/community/vendors/{vendorId}/comment` | Customer JWT | Creates or replaces that customer's unique rating/comment for the vendor. |
| BUY-05 | `POST /api/community/vendors/{vendorId}/reports` | Customer JWT | Creates a `PENDING` report; supplied slot/permit references must belong to the vendor. |

The authenticated handlers re-read the user from StreetBizDB and require both
role `CUSTOMER` and account status `ACTIVE`; trusting the JWT role alone is not
sufficient. Evidence images use the existing authenticated
`POST /api/uploads/evidence` endpoint before the report is submitted.

Run the checks from `StreetBiz-BE`:

```powershell
dotnet build StreetBiz.Backend.sln --no-restore
dotnet test StreetBiz.Backend.sln --no-build --no-restore
dotnet run --project src/StreetBiz.API --launch-profile http
```

Then open `http://localhost:5023/swagger`. A real BUY-02 request writes a scan
log, while BUY-04 and BUY-05 write customer data; use a disposable test account
and database when manually exercising those routes.
