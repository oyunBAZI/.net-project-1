# Architecture

## Layers

```
AxiomLedger.Web ─┐
AxiomLedger.Cli ─┴─► AxiomLedger.Infrastructure ─► AxiomLedger.Application ─► AxiomLedger.Domain
```

- **Domain** — entities, enums, money rounding, document calculations, journal validation, permissions,
  password hashing. No EF Core, ASP.NET Core or I/O.
- **Application** — use cases and the only write path to the ledger. Depends on `IAccountingDbContext`
  (implemented by the EF context) and `IClock`, so it is testable and provider-agnostic.
- **Infrastructure** — EF Core mapping, provider selection, schema bootstrap, demo data.
- **Web** — Razor Pages, cookie authentication, current-user adapter, view helpers and the design system.
- **Cli** — administration and report exports using the same services with a system user.

## The posting pipeline

`IJournalPoster.PostAsync` is the single gateway for every financial effect. Invoices, bills, payments,
bank entries, year-end close and manual journals all pass through it.

1. Reject already-posted, reversed or void entries.
2. Stamp company on lines, recalculate totals, validate double-entry rules.
3. Load accounts and reject missing, summary or inactive accounts.
4. Enforce the company lock date.
5. Find or create the accounting period; reject closed periods and soft-closed periods for unauthorised users.
6. Apply approval rules: threshold, approval permission and segregation of duties.
7. Allocate the journal number under a per-company lock (unique index as the final guard).
8. Mark posted, stamp actor and time, default currency/rate, attach to the context.

Callers own the transaction boundary (`SaveChangesAsync`), so a document and its journal commit together.

## Documents

- `SalesInvoice` / `Bill` recalculate line net, discount, tax (inclusive or exclusive), totals and balance due
  from their lines. Status transitions are explicit: draft → issued/received → partially paid → paid, or void.
- Numbers are allocated when the document is issued/received; drafts have no number.
- Posting builds a balanced journal: AR/AP control, revenue/expense lines, tax lines, and FX gain/loss when the
  settlement rate differs from the document rate.
- Payments allocate against open documents, cap automatic allocation at the cash received, and keep any
  remainder as an unapplied credit (customer) or prepayment (vendor).

## Reports

`ReportingService` queries **posted journal lines only**, grouped by account, and maps account groups to
statement sections. This guarantees:

- Trial balance debits equal credits.
- Balance sheet assets equal liabilities plus equity (including current-year earnings).
- P&L net profit equals the movement in equity for the period.
- Cash flow closing cash equals the sum of cash-account balances.

Aging is document-based (open invoices/bills with exchange rates normalised to base currency) and exposes
unapplied credits separately.

## Persistence

- `AccountingDbContext` maps every aggregate, with explicit decimal precision and indexes on hot paths
  (company + date, account + entry, document number, party).
- Providers: SQLite (default), PostgreSQL, SQL Server — selected by configuration.
- Schema creation uses `EnsureCreated` today; EF migrations are the first item of Phase 8. Take a backup before
  upgrading.
- SQLite cannot `SUM` decimal columns. Report aggregates are therefore summed as `double` in SQL and rounded back
  to `decimal` (exact to the cent at realistic ledger sizes); other providers use the same code path.
- `Math.Abs` is not translatable by SQLite either, so tolerance filtering happens in memory over narrow windows.

## Concurrency and numbering

- `CompanyLock` serialises document-number allocation per company inside a process.
- Unique indexes on journal, invoice, bill and payment numbers make multi-instance collisions fail safely
  instead of duplicating numbers.
- Postings are append-only; the ledger is never updated in place except through explicit status transitions.

## Security

- Cookie authentication, HttpOnly/SameSite=Lax, secure cookies when HTTPS is used.
- PBKDF2-SHA256 (210k iterations, per-user salt), rehash on login, lockout after repeated failures.
- Server-side permission checks (`PermissionGuard`) for every mutating use case; UI hides what the role cannot do
  but is not the security boundary.
- Every query is scoped by `CompanyId`; membership and active status are validated at sign-in and on switch.
- Antiforgery tokens on forms; audit log records actor, IP, entity, action and details.

## Extensibility seams

| Seam | Use |
| --- | --- |
| `IJournalPoster` | Add new posting sources without duplicating controls |
| `IAccountingDbContext` | Swap persistence or build read-only projections |
| `IClock` | Deterministic tests and period logic |
| `ICurrentUser` | Web, CLI and background-job identities |
| `ChartOfAccountsTemplate` | Industry-specific charts |
| `TaxCode` data | Jurisdiction packs without code changes |
| `BankingService` CSV parser | Additional statement formats |

## Testing strategy

Each test creates a throwaway SQLite database, a company with a full chart of accounts, and a mutable current
user. Tests exercise the real services (no mocks of the ledger) and assert on domain state, posted balances and
report output. The suite is deliberately integration-first because the invariants live in the interaction
between documents, the poster and the database.

## Known limitations

- Single-process number locking; multi-instance deployments rely on unique indexes for safety (retry guidance
  planned).
- No EF migrations yet (schema bootstrap only).
- No REST API, SSO, attachments or e-invoicing yet — see the roadmap.
- UI is English-only; dates are stored as UTC and rendered in the server's local time.
- Departments are the only analytical dimension today; projects and richer dimensions are planned.
