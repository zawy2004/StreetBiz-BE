# StreetBiz Assistant

## ADR — 2026-10-06

The user explicitly authorized SQL Server and the existing database-first workflow for this chatbot task, as an exception to the root AGENTS.md PostgreSQL requirement. No database engine migration is in scope. Schema changes belong in `db/StreetBiz_SQL_Server.sql` and matching EF mappings. No existing database is recreated or seeded by the chatbot or by application startup.

Business tools are read-only and use server-resolved actors. Chatbot history, feedback and technical audit are separate from person-to-person chat and from compliance decisions. Marketplace tools remain disabled/out of scope.

Generation stays inside the authenticated HTTP request; SignalR is optional delivery of progress, never the system of record. Persistence and REST snapshots are authoritative. No `Task.Run` background generation with an ambient HttpContext.

The first deployment targets a single API instance. Database ownership, idempotency and generation leases protect records; streaming/cancellation subscriptions are process-local. Scale-out requires a SignalR backplane and shared cancellation/quota coordination before enabling multiple API replicas.

## Development schema activation — 2026-10-07

The user separately authorized adding only the three chatbot tables to the existing development database, preserving business data. The exact CREATE TABLE/index blocks were extracted from the canonical schema and executed together in a transaction against `localhost/StreetBizDB`. The pre-existing 58 tables were retained; precisely three tables were added. No recreate, seed, ALTER, DROP, or business-row UPDATE/DELETE was run. This one-time authorized operation does not change the repository's normal database-first policy.
