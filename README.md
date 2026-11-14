<div align="center">

# StreetBiz Backend

**REST API and real-time hub for StreetBiz, the sidewalk-vendor management and marketplace platform for the wards of Đà Nẵng.**

[![CI](https://github.com/zawy2004/StreetBiz-BE/actions/workflows/ci.yml/badge.svg?branch=develop)](https://github.com/zawy2004/StreetBiz-BE/actions/workflows/ci.yml)
[![Deploy](https://github.com/zawy2004/StreetBiz-BE/actions/workflows/deploy-vps.yml/badge.svg?branch=main)](https://github.com/zawy2004/StreetBiz-BE/actions/workflows/deploy-vps.yml)
![.NET 8](https://img.shields.io/badge/.NET-8.0_LTS-512BD4?logo=dotnet&logoColor=white)
![SQL Server](https://img.shields.io/badge/SQL_Server-2022-CC2927?logo=microsoftsqlserver&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-Compose-2496ED?logo=docker&logoColor=white)

[Web app](https://www.streetbiz.id.vn) · [Frontend repo](https://github.com/zawy2004/StreetBiz-FE) · [Mobile app repo](https://github.com/zawy2004/StreetBiz-App) · [Deployment guide](docs/deploy-vps.md)

</div>

---

## Contents

- [Overview](#overview)
- [Features](#features)
- [Tech stack](#tech-stack)
- [Architecture](#architecture)
- [Getting started](#getting-started)
- [Configuration](#configuration)
- [Database](#database)
- [Testing](#testing)
- [Deployment](#deployment)
- [Project structure](#project-structure)
- [Documentation](#documentation)

## Overview

StreetBiz digitises how a ward licenses and supervises street vendors, and gives those
vendors an online storefront. One API serves five roles:

| Role | What they do |
| --- | --- |
| **Vendor** (household business) | Registers the business with eKYC, rents a sidewalk slot, pays fees, runs a storefront, takes orders, files household tax |
| **Customer** | Finds vendors on the map, checks a permit by QR, orders and pays, rates and reports |
| **Ward authority** | Reviews registrations, manages slots and pricing zones, issues digital permits, collects fees, records violations |
| **Platform administrator** | Manages categories, moderates content, resolves complaints, sees the platform dashboard |
| **Guest** | Browses the public map and scans a printed price-board QR code |

## Features

| Area | Highlights |
| --- | --- |
| **Identity** | Phone + password or phone + OTP sign-in, OTP by SMS through eSMS.vn, JWT with rotating refresh tokens, session and login history, rate limiting |
| **Onboarding** | Business registration with evidence files and FPT.AI eKYC, ward review queue with full audit trail |
| **Sidewalk slots** | Pricing zones, slot proposals, rental applications, contracts, renewals, transfers, address conflicts, geofencing with Nominatim |
| **Permits** | Digital permits with signed QR codes and a public verification page |
| **Fees and payments** | Fee schedules, reminders, invoices, payOS (VietQR) paid straight to each household or ward, MoMo and ZaloPay adapters |
| **Marketplace** | Storefronts, menus, cart, prepaid orders, pickup range checks, refunds, order complaints, ratings |
| **Real time** | SignalR for order status and chat, with polling fallback on the clients |
| **AI assistance** | Gemini and Groq with key rotation for seller, customer and ward assistants, a chatbot with voice, compliance suggestions |
| **Household tax** | Revenue tracking and tax overview for household businesses |
| **Reporting** | Ward collection reports and dashboards, platform dashboard, Excel, Word and PDF export |

## Tech stack

| Layer | Technology |
| --- | --- |
| Runtime | .NET 8 (LTS), ASP.NET Core Web API, SignalR |
| Application | MediatR 12 (CQRS), FluentValidation 12, AutoMapper |
| Data | Entity Framework Core 8 (database-first), SQL Server 2022 |
| Logging and health | Serilog, ASP.NET Core health checks |
| Documents and images | ClosedXML, DocumentFormat.OpenXml, PdfSharpCore, ImageSharp |
| Integrations | eSMS.vn, FPT.AI, Gemini, Groq, payOS, MoMo, ZaloPay, Nominatim |
| Tests | xUnit, FluentAssertions, Moq, WebApplicationFactory, SQLite in-memory |
| Delivery | Docker, GitHub Actions, GHCR, Caddy (automatic HTTPS) |

## Architecture

Clean Architecture with dependencies pointing inward:

~~~text
             ┌──────────────────────────┐
             │       StreetBiz.API       │  Controllers, SignalR hubs, auth, rate limits
             └────────────┬─────────────┘
                          │
        ┌─────────────────┴──────────────────┐
        ▼                                    ▼
┌────────────────────┐          ┌────────────────────────┐
│ StreetBiz.          │◄─────────│ StreetBiz.             │  EF Core, SMS, payments,
│ Application         │          │ Infrastructure         │  AI, storage, geocoding
│ (use cases, CQRS)   │          └────────────────────────┘
└─────────┬──────────┘
          ▼
┌────────────────────┐
│ StreetBiz.Domain    │  No framework dependencies
└────────────────────┘
~~~

Each use case is a MediatR command or query with its own FluentValidation validator, run by
a shared validation pipeline behaviour. See [docs/architecture.md](docs/architecture.md).

## Getting started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- SQL Server 2022 (LocalDB, Developer edition or the Docker image)
- Optional: Docker Desktop, `dotnet-ef` 8.x

### 1. Build the database

~~~powershell
# Stop the API first: an open connection blocks DROP DATABASE.
powershell -ExecutionPolicy Bypass -File scripts/setup-local-db.ps1 -Recreate
~~~

This builds `StreetBizDB` from `db/StreetBiz_SQL_Server.sql` and loads the demo seed.

> [!WARNING]
> `-Recreate` drops the database. Never run it against a shared or production database.

### 2. Point the API at it

~~~powershell
dotnet user-secrets --project src/StreetBiz.API set "ConnectionStrings:StreetBizDB" "<connection-string>"
~~~

### 3. Run

~~~powershell
dotnet run --project src/StreetBiz.API --launch-profile https
~~~

| URL | Purpose |
| --- | --- |
| `https://localhost:7147/swagger` | Swagger UI (Development only) |
| `https://localhost:7147/health` | Health check, including SQL Server |

### Demo accounts

The demo seed creates these accounts for a **local** database. Every password is `Password123!`.

> [!IMPORTANT]
> These passwords are public. A server built with the demo seed must change every one of them before
> anyone else gets the link (see [docs/deploy-vps.md](docs/deploy-vps.md)); the production accounts do not use them.

| Role | Phone |
| --- | --- |
| Platform administrator | `0900000001` |
| Ward authority (Hải Châu) | `0983000001` |
| Vendor, approved with a permit and storefront | `0905000101` |
| Vendor, registration awaiting review | `0905000102` |
| Customer | `0905000201` |

The full list and the scenario behind it are in [docs/database.md](docs/database.md#demo-data).

### Run with Docker

~~~powershell
Copy-Item .env.example .env   # then fill in the placeholders
docker compose up --build
~~~

The Compose SQL Server container only starts the engine. It never creates, migrates or
seeds `StreetBizDB`.

## Configuration

Secrets never go into `appsettings*.json` in Git. Locally they live in user-secrets or the
ignored `appsettings.Development.json`. On the server they go in `api.env`, with `__` as the
section separator (for example `Sms__ApiKey`).

| Section | Purpose | Required in Production |
| --- | --- | :---: |
| `ConnectionStrings:StreetBizDB` (or `STREETBIZ_DB_CONNECTION`) | SQL Server | ✅ |
| `Jwt:SigningKey` | Access-token signing | ✅ |
| `Otp:HashKey` | HMAC key for stored OTP codes | ✅ |
| `Permit:SigningKey` | Signs permit QR codes | ✅ |
| `OrderPickup:SigningKey` | Signs order pickup codes | ✅ |
| `Sms` | eSMS.vn `ApiKey`, `SecretKey`, `SmsType` (8 = fixed number) | For OTP |
| `Cors:AllowedOrigins` | Web origins allowed to call the API | ✅ |
| `Payments:PayOs` | Credential encryption key, webhook and return URLs | For payOS |
| `AiCompliance` | FPT.AI eKYC, Gemini and Groq keys | For AI features |

The API refuses to start in Production when a required key is missing.
[deploy/vps/api.env.example](deploy/vps/api.env.example) lists every setting with comments.

## Database

`StreetBizDB` is **database-first**. The API never creates or changes the schema.

| Script | Contents |
| --- | --- |
| `db/StreetBiz_SQL_Server.sql` | Schema, reference data (94 wards of Đà Nẵng), EF history stamp |
| `db/StreetBiz_Demo_Seed.sql` | Demo accounts and scenario (development only) |

A schema change edits those two files and the EF model together. EF migrations,
`EnsureCreated` and `Migrate` are not used. See [docs/database.md](docs/database.md).

## Testing

~~~powershell
dotnet test StreetBiz.Backend.sln -c Release
~~~

| Project | Covers |
| --- | --- |
| `StreetBiz.Domain.Tests` | Domain rules |
| `StreetBiz.Application.Tests` | Handlers, validators and pipeline behaviours |
| `StreetBiz.Infrastructure.Tests` | Repositories on SQLite, adapters (eSMS, payOS, AI) with fake HTTP |
| `StreetBiz.API.Tests` | HTTP endpoints through `WebApplicationFactory` |

CI runs the whole suite and builds the Docker image on every pull request. Manual test
scripts and a Postman collection are in [docs/testing/](docs/testing/).

## Deployment

~~~text
push to main ──► GitHub Actions: test ─► build image ─► push to GHCR ─► SSH deploy
                                                                           │
                    VPS (Ubuntu, Docker Compose)                            ▼
                    ┌──────────┐   ┌───────────────┐   ┌──────────────────────┐
  HTTPS ──────────► │  Caddy   │──►│ StreetBiz API │──►│ SQL Server 2022      │
                    └──────────┘   └───────────────┘   │ (internal network)   │
                                                       └──────────────────────┘
~~~

- `deploy/vps/deploy.sh` pulls the new image and rolls back by itself when the health check fails.
- `deploy/vps/backup.sh` backs up the database and uploaded files every night.
- The web app is hosted separately on Vercel.

The full server setup is in [docs/deploy-vps.md](docs/deploy-vps.md).

## Project structure

~~~text
StreetBiz-BE/
├── src/
│   ├── StreetBiz.Domain/           Entities and domain rules
│   ├── StreetBiz.Application/      Use cases (commands, queries, validators)
│   ├── StreetBiz.Infrastructure/   EF Core, external services, storage
│   └── StreetBiz.API/              Controllers, SignalR hubs, startup
├── tests/                          Unit, integration and API tests
├── db/                             Schema and demo seed scripts
├── deploy/vps/                     Production Compose stack, Caddyfile, deploy and backup scripts
├── scripts/                        Local database setup and end-to-end helpers
├── docs/                           Feature, operations and testing documentation
├── Dockerfile
└── docker-compose.yml              Local development stack
~~~

## Documentation

| Topic | Documents |
| --- | --- |
| Foundations | [Architecture](docs/architecture.md) · [Database](docs/database.md) · [Deployment](docs/deploy-vps.md) |
| Vendors and wards | [Auth and onboarding](docs/auth-vendor-onboarding.md) · [Sidewalk slot rental](docs/sidewalk-slot-rental.md) · [Ward slot workflows](docs/ward-slot-workflows.md) · [Registration integrity](docs/ward-registration-integrity-and-report-summary.md) |
| Money | [Fees, payments and invoicing](docs/fee-payment-invoicing.md) · [payOS](docs/payos-payments.md) · [Household tax](docs/household-tax.md) · [Business calendar](docs/business-calendar.md) |
| Marketplace | [Customer discovery](docs/customer-discovery.md) · [Community vendor workflows](docs/community-vendor-workflows.md) · [Commerce orders](docs/commerce-order-workflows.md) · [Order tracking and pickup range](docs/order-tracking-pickup-range.md) · [Marketplace completion](docs/marketplace-completion-workflows.md) |
| Administration | [Platform administration](docs/platform-administration-workflows.md) |
| AI | [AI assistance](docs/ai-marketplace-assistance.md) · [Chatbot architecture](docs/chatbot-architecture.md) · [Chatbot runbook](docs/chatbot-runbook.md) · [Voice design](docs/chatbot-voice-design.md) |
| Testing | [docs/testing/](docs/testing/) |

---

<div align="center">
<sub>StreetBiz is a capstone project. The frontend and mobile app live in their own repositories.</sub>
</div>
