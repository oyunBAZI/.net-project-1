# Axiom Ledger

A cross-platform, double-entry accounting suite for small, medium and large businesses, written in C#.

Axiom Ledger ships as a **web application** (ASP.NET Core Razor Pages) and a **command-line tool**, both running
on Windows, Linux and macOS from the same codebase and the same database. It covers the ledger, receivables,
payables, payments, banking and reconciliation, financial reporting, period close, multi-company and
multi-currency operations, with role-based access control, an approval workflow and an append-only audit trail.

> **Platform note.** The original brief asked for ".NET Framework". .NET Framework only runs on Windows, which
> would contradict the Windows/Linux/macOS requirement. Axiom Ledger therefore targets **.NET 8**, Microsoft's
> current long-term-support runtime, which is cross-platform and is the direct successor to .NET Framework. The
> C# language, the BCL, ASP.NET Core and Entity Framework Core are all used; the solution simply runs everywhere.

---

## What is included

### Ledger

- True double-entry journal with immutable posted entries; corrections are made with reversing entries.
- Manual journals with draft → submit → approve → post workflow (configurable threshold and segregation of duties).
- Automatic numbering per company and document type, protected by unique indexes.
- Fiscal years, monthly accounting periods (open / soft-closed / closed), company lock date.
- Multi-company membership with per-company roles; multi-currency with dated exchange rates and FX gain/loss.
- Full audit trail of every action with user, IP, entity and timestamp.

### Receivables and payables

- Customers and vendors with terms, credit limits, tax IDs and opening activity.
- Sales invoices and vendor bills with discounts, tax codes (inclusive or exclusive), departments and FX.
- Issue/receive, void, print-ready views, open-document lists and party statements.
- Receipts and payments with partial allocations, unapplied credits, over-payment protection, FX settlement.
- Aging reports (current, 1–30, 31–60, 61–90, 90+) with unapplied credits shown separately.

### Banking

- Multiple bank accounts mapped to GL cash accounts.
- CSV statement import with duplicate detection, auto-matching and manual match/unmatch.
- Create ledger entries directly from statement lines; exclusion of non-ledger lines.
- Reconciliation summary (book vs statement, outstanding cheques, deposits in transit) and completion snapshots.

### Reporting

- Trial balance, profit & loss (with comparative period), balance sheet, cash flow (operating / investing /
  financing), general ledger, tax summary and detailed tax by period, receivables and payables aging.
- Dashboard with cash, AR/AP, revenue, expenses, net profit, VAT liability, overdue invoices, pending approvals,
  unreconciled bank lines and a 12-month trend.
- Every report is derived from posted journal lines, so it reconciles to the trial balance by construction.
- CSV export from the CLI and report screens.

### Administration

- First-run browser setup, cookie authentication, PBKDF2-SHA256 password hashing with rehash-on-login.
- Roles: Owner, Admin, Accountant, Bookkeeper, Approver, Sales, Purchasing, Auditor, Viewer.
- Company profile, accounting preferences, lock date, tax codes, currencies and exchange rates, users and
  roles, periods and year-end close, audit trail browser.
- Year-end close with a pre-close checklist and an automatic closing entry into retained earnings.

### Demo data

The dashboard can seed the *Northwind Trading Co.* demo company: chart of accounts, customers, vendors, five
months of invoices, bills, receipts, payments, bank statement and approval queue. It is a realistic way to
explore every report immediately.

---

## Repository layout

```
AxiomLedger.sln
src/
  AxiomLedger.Domain/          Entities, enums, money, invariants, permissions, password hashing
  AxiomLedger.Application/     Use cases: posting, sales, purchasing, payments, banking, reporting, year-end
  AxiomLedger.Infrastructure/  EF Core context, provider wiring, schema bootstrap, demo data
  AxiomLedger.Web/             Razor Pages UI, authentication, design system
  AxiomLedger.Cli/             Administration and report export commands
tests/
  AxiomLedger.Tests/           xUnit integration tests over a real SQLite database
docs/
  PLAN.md                      Deep phased delivery plan and roadmap
  ARCHITECTURE.md              Layering, posting pipeline, invariants, extension points
```

Dependency direction is strictly inward: `Web`/`Cli` → `Infrastructure` → `Application` → `Domain`.
The domain has no framework dependencies.

---

## Quick start

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (8.0.x)
- Optional: Docker, PostgreSQL 14+ or SQL Server 2019+ for larger deployments

### Build and test

```bash
dotnet restore
dotnet build AxiomLedger.sln
dotnet test tests/AxiomLedger.Tests/AxiomLedger.Tests.csproj
```

### Run the web application

```bash
dotnet run --project src/AxiomLedger.Web
```

Open the printed URL (default `http://localhost:5000`). On a fresh database you are taken to **Setup**, where
you create the first owner account and company. Sign in, then use **Load sample company** on the dashboard to
explore a fully populated ledger.

The database file `axiomledger.db` is created next to the working directory by default.

### Run the CLI

```bash
dotnet run --project src/AxiomLedger.Cli -- help
```

Typical automation sequence:

```bash
# create the schema and the first owner + company
axiomledger setup --company "Acme Ltd" --email owner@acme.test --password "Str0ngPass!" --currency USD

# seed a complete demo company for that owner
axiomledger seed-demo --email owner@acme.test

# export reports for a BI tool or month-end pack
axiomledger report trial-balance --company Acme --from 2026-01-01 --to 2026-12-31 --csv > tb.csv
axiomledger report profit-loss   --company Acme --from 2026-01-01 --to 2026-12-31 --csv > pl.csv
axiomledger report aging         --company Acme --to 2026-12-31 --csv > ar-aging.csv
```

| Command | Purpose |
| --- | --- |
| `init` | Create the schema and seed currencies |
| `setup` | Create the first owner and a company |
| `seed-demo` | Seed the Northwind demo company |
| `company list` | List companies with user and account counts |
| `users list` / `users create` / `users password` | User administration |
| `accounts` | Chart of accounts |
| `journal` | Journal listing with filters |
| `report trial-balance \| profit-loss \| balance-sheet \| cash-flow \| aging \| tax \| general-ledger` | Statements and exports |
| `version` | Runtime and product version |

Global options: `--connection`, `--provider` (`sqlite`, `postgresql`, `sqlserver`), `--as-user`.

---

## Configuration

Configuration is standard ASP.NET Core configuration (`appsettings.json`, environment variables, command line).

```json
{
  "Database": { "Provider": "sqlite" },
  "ConnectionStrings": {
    "Default": "Data Source=axiomledger.db"
  }
}
```

Environment variable overrides:

```bash
export Database__Provider=postgresql
export ConnectionStrings__Default="Host=db;Database=axiom;Username=axiom;Password=..."
dotnet run --project src/AxiomLedger.Web
```

Supported providers:

| Provider | Value | Recommended for |
| --- | --- | --- |
| SQLite | `sqlite` (default) | Small businesses, evaluation, single-server installs |
| PostgreSQL | `postgresql` | Medium and large deployments, managed cloud databases |
| SQL Server | `sqlserver` | Organisations standardised on Microsoft data platform |

The schema is created automatically on start-up (`EnsureCreated`). Entity Framework migrations are on the
roadmap for controlled production upgrades; take a backup before upgrading between releases.

### Docker

```bash
docker build -t axiomledger .
docker run -d --name axiomledger -p 8080:8080 -v axiom-data:/data axiomledger
```

The container stores its SQLite database in the `/data` volume and listens on port 8080. Point
`ConnectionStrings__Default` at PostgreSQL or SQL Server for multi-instance deployments.

---

## Security model

- Cookie authentication with HttpOnly, SameSite=Lax cookies; secure cookies when served over HTTPS.
- Passwords hashed with PBKDF2-SHA256, 210,000 iterations, per-user salt, automatic rehash on sign-in.
- Account lockout after repeated failures; deactivated users and memberships are rejected at sign-in.
- Permission checks in the application layer (`PermissionGuard`), not only in the UI.
- Antiforgery tokens on every mutating form; same-site cookies.
- Company data is always scoped by `CompanyId`; a user must be an active member to read or change it.
- Posted journals are immutable; the books lock date and closed periods prevent back-dated changes.
- Segregation of duties: a preparer cannot approve their own journal when enabled.
- Append-only audit log with actor, IP address, entity, action and details.

For internet-facing deployments, terminate TLS at a reverse proxy, set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`,
enable HSTS (automatic outside Development), and keep the database on a private network.

---

## Testing

The test suite runs the real application services and EF Core against a throwaway SQLite database per test:

```bash
dotnet test
```

Covered today: balanced/unbalanced posting, closed periods, lock dates, reversals, RBAC denial, invoice tax
posting, partial and full receipts, unapplied credits, over-allocation rejection, vendor bills, trial balance,
balance sheet, profit & loss, cash flow, aging, dashboard aggregation, party/payment balances, year-end close
and the pre-close checklist.

---

## Roadmap

`docs/PLAN.md` contains the full phased plan, including the next enterprise capabilities: EF migrations,
REST API, SSO (OIDC/SAML), consolidation, budgeting, fixed assets, inventory, payroll integration, e-invoicing,
bank feeds, attachments, configurable approval matrices, granular permissions, and observability/backup
hardening. `docs/ARCHITECTURE.md` documents the posting pipeline and extension seams.
