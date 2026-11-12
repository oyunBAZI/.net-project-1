# Axiom Ledger — delivery plan

Status: living document. Phases 0–7 are implemented in this repository; Phases 8–12 are the enterprise roadmap.

## 1. Product vision

A dependable accounting system that a two-person shop and a multi-entity group can both run on the same
engine, without compromising the ledger. The non-negotiables:

1. **Correctness before convenience.** Every financial figure comes from posted, balanced journal lines.
2. **Auditability.** Nothing disappears. Posted entries are immutable; corrections are reversing entries;
   every action is logged.
3. **Cross-platform by construction.** One codebase, one database schema, running on Windows, Linux and macOS.
4. **Scales by configuration, not by fork.** Small businesses get a single SQLite file; large organisations get
   PostgreSQL/SQL Server, multiple companies, roles, approval thresholds and closed periods.
5. **Boring, inspectable technology.** C#, .NET 8, ASP.NET Core, EF Core. No hidden magic in the money path.

## 2. Personas and fit

| Segment | Typical need | How Axiom Ledger fits |
| --- | --- | --- |
| Small business (1–20 staff) | Invoicing, expenses, VAT, bank reconciliation, simple reports | Single-company install, SQLite, default roles, demo data, year-end close |
| Medium business (20–250) | Multiple users and roles, approvals, departmental reporting, multi-currency | RBAC, approval workflow, departments, FX, PostgreSQL, audit trail |
| Large / group (250+) | Multi-entity, strict controls, integrations, reporting packs | Multi-company membership, lock dates, period close, segregation of duties, CSV/report exports, API and SSO on roadmap |

## 3. Accounting model invariants

These are enforced in code, not documentation:

- A journal must have at least two lines, at least one debit and one credit, no negative amounts, no line with
  both debit and credit, and total debits equal total credits within one cent.
- Only postable, active accounts of the company can be used.
- Posting requires an open period and a date after the company lock date.
- Manual journals above the approval threshold require approval when the workflow is enabled; the preparer
  cannot approve their own journal under segregation of duties.
- Posted entries cannot be edited; reversals create a mirrored posted entry and link both directions.
- Document numbers are allocated per company under a company-level lock and protected by unique indexes.
- Receivable/payable balances are derived from documents and allocations, never stored as editable figures.
- Reports aggregate posted journal lines only.

## 4. Phases

### Phase 0 — Repository and foundation ✅
Solution, layered projects, CI, cross-platform target, `.gitignore`, container image.
**Acceptance:** clean build on Windows/Linux/macOS; tests run in CI.

### Phase 1 — Core ledger ✅
Accounts and chart templates, fiscal years and periods, journals, posting gateway, numbering, audit log,
company and membership model, lock date.
**Acceptance:** balanced entries post; unbalanced/closed/locked entries rejected; trial balance balances.

### Phase 2 — Receivables and payables ✅
Parties, invoices, bills, tax codes, discounts, allocations, receipts/payments, unapplied credits, FX gain/loss,
voiding, aging.
**Acceptance:** issuing an invoice posts AR/revenue/tax; partial receipts allocate only cash received; over
allocation rejected; aging reconciles to AR control account.

### Phase 3 — Banking ✅
Bank accounts, CSV import with deduplication, auto-match, manual match/unmatch, entry creation from statement
lines, reconciliation summary and completion.
**Acceptance:** import is idempotent; matching respects amount tolerance and date window; reconciliation
difference explains book vs statement.

### Phase 4 — Reporting ✅
Trial balance, P&L with comparatives, balance sheet, cash flow, general ledger, tax, aging, dashboard, party
statements, CSV export.
**Acceptance:** all statements derive from posted lines; balance sheet balances; cash flow closing equals cash
account balances.

### Phase 5 — Controls and administration ✅
Roles and permissions, approval workflow, segregation of duties, lock date, period close, year-end close with
checklist, company settings, tax/currency/user admin, audit browser.
**Acceptance:** role denial is enforced server-side; year-end zeroes P&L and moves profit to retained earnings;
checklist blocks close on drafts, pending approvals or imbalance.

### Phase 6 — Experience ✅
Design system, dashboard, list/detail/edit flows for every document, responsive navigation, empty states,
inline validation, print-friendly documents, sample company.
**Acceptance:** a new user can set up, seed demo data and produce a report without documentation.

### Phase 7 — Verification and delivery ✅
xUnit integration suite, CLI administration and exports, Docker image, README, architecture and plan documents.
**Acceptance:** `dotnet test` green; CLI creates a company, seeds demo data and exports reports on SQLite.

### Phase 8 — Production data lifecycle (next)
- EF Core migrations with versioned upgrade scripts and a migration lock.
- Backup/restore commands (`axiomledger backup`, `restore`) and documented RPO/RTO.
- Data retention and archival of closed years; audit export (CSV/JSON) with hash chaining.
- Health/readiness probes, structured logging, OpenTelemetry metrics and tracing.
- Seed/reference data versioning (currencies, tax templates, chart templates).

**Acceptance:** upgrade from N-1 release on PostgreSQL and SQL Server in staging without data loss; restore
drill documented and timed.

### Phase 9 — Integration surface
- Versioned REST API (`/api/v1`) with token authentication, idempotency keys and OpenAPI description.
- Webhooks for document events (issued, paid, posted, closed).
- CSV/Excel import for parties and opening balances; bank feed adapters (OFX/CSV/PSD2 providers).
- Attachment storage for documents (local disk or S3-compatible object storage).
- E-invoicing adapters (UBL/Peppol, country-specific mandates) behind a provider interface.

**Acceptance:** API parity for invoices, bills, payments and reports; idempotent retries; signed webhooks.

### Phase 10 — Enterprise identity and governance
- OIDC/SAML single sign-on, SCIM provisioning, MFA.
- Configurable approval matrices (amount, department, document type), delegation and escalation.
- Granular permissions per module and per company; read-only auditor export role.
- Multi-entity consolidation, intercompany eliminations, group reporting and revaluation.
- Field-level audit history and immutable ledger snapshots per close.

**Acceptance:** SSO sign-in and role mapping; approval matrix covered by tests; consolidated statements tie to
entity ledgers after eliminations.

### Phase 11 — Extended accounting
- Fixed assets: registers, depreciation methods, disposal, automatic journals.
- Inventory: weighted average / FIFO costing, stock movements, COGS posting.
- Budgeting and variance reporting; project/dimension accounting beyond departments.
- Recurring documents, dunning, payment runs, expense claims, payroll journal import.
- Tax engines: VAT/GST returns, EC sales lists, withholding, reverse charge, jurisdictional rules.

**Acceptance:** each module posts through the same journal gateway and appears in existing reports without
special cases.

### Phase 12 — Scale and operations
- Performance programme: keyset pagination, covering indexes, read models for dashboards, load tests at
  10M+ journal lines.
- Horizontal scaling with PostgreSQL/SQL Server, connection pooling, background job processing for imports
  and report packs.
- Per-tenant encryption strategy, secrets management, least-privilege database roles.
- SLOs, alerting, on-call runbooks, disaster-recovery exercises.

**Acceptance:** published benchmark results; documented SLOs met in staging; DR exercise passes.

## 5. Non-functional targets

| Area | Target |
| --- | --- |
| Posting latency | p95 < 150 ms for a 20-line journal on PostgreSQL |
| Report latency | Trial balance / P&L < 2 s for 1M journal lines with proper indexes |
| Availability | 99.9% for hosted deployments (Phase 12) |
| Data safety | Zero tolerance for unbalanced postings; backups verified by restore drills |
| Accessibility | Keyboard-navigable forms, labelled inputs, sufficient contrast |
| Localisation | UTC storage, ISO dates in exports, per-company currency and fiscal calendar; UI translation roadmap |

## 6. Risks and mitigations

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Schema drift without migrations | Upgrade failures | Phase 8 migrations plus staging upgrade tests |
| SQLite write contention | Slowdowns for larger firms | Document PostgreSQL/SQL Server for >10 concurrent writers; connection tuning |
| Provider differences (decimal aggregation, functions) | Report failures | Integration tests per provider; aggregates normalised in application code where needed |
| Tax rules vary by jurisdiction | Compliance gaps | Tax codes are data-driven; country packs and e-invoicing adapters on roadmap |
| Permission regressions | Unauthorised access | Server-side guards plus RBAC tests in the suite |
| Report performance at scale | Slow month-end | Indexed queries, read models, benchmark suite in Phase 12 |

## 7. Definition of done (per feature)

1. Domain invariants enforced and unit/integration tested.
2. Permission checked server-side and covered by a denial test.
3. Audit entry written for state changes.
4. Report impact verified against the trial balance.
5. UI states: empty, validation error, success, forbidden.
6. Documentation updated (README, plan, architecture as needed).
