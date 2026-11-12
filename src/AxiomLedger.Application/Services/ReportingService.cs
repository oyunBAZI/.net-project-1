using AxiomLedger.Application.Models;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

/// <summary>
/// Read model over the posted ledger. Reports are always derived from journal lines, so they reconcile to the
/// trial balance by construction.
/// </summary>
public sealed class ReportingService
{
    private readonly IAccountingDbContext _db;
    private readonly IClock _clock;

    public ReportingService(IAccountingDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    private IQueryable<JournalLine> Posted(Guid companyId, DateOnly? from, DateOnly? to)
    {
        var query = from l in _db.JournalLines
                    join j in _db.JournalEntries on l.JournalEntryId equals j.Id
                    where j.CompanyId == companyId && j.Status == JournalStatus.Posted
                    select new { Line = l, Entry = j };

        if (from.HasValue) query = query.Where(x => x.Entry.EntryDate >= from.Value);
        if (to.HasValue) query = query.Where(x => x.Entry.EntryDate <= to.Value);

        return query.Select(x => x.Line);
    }

    private sealed record Sums(decimal Debit, decimal Credit)
    {
        public decimal Net => Debit - Credit;
    }

    private async Task<Dictionary<Guid, Sums>> SumsAsync(Guid companyId, DateOnly? from, DateOnly? to,
        CancellationToken ct)
    {
        // SQLite cannot SUM decimal columns, so the aggregate is taken as double in SQL and rounded back to
        // decimal. Amounts stay exact to the cent well beyond any realistic ledger size.
        var rows = await Posted(companyId, from, to)
            .GroupBy(l => l.AccountId)
            .Select(g => new
            {
                AccountId = g.Key,
                Debit = g.Sum(x => (double)x.Debit),
                Credit = g.Sum(x => (double)x.Credit)
            })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.AccountId, r => new Sums((decimal)r.Debit, (decimal)r.Credit));
    }

    public async Task<TrialBalanceReport> TrialBalanceAsync(Guid companyId, DateOnly from, DateOnly to,
        bool includeZeroBalances = false, CancellationToken ct = default)
    {
        if (to < from) throw new DomainValidationException("The end date must be on or after the start date.");

        var opening = await SumsAsync(companyId, null, from.AddDays(-1), ct);
        var period = await SumsAsync(companyId, from, to, ct);
        var accounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId)
            .OrderBy(a => a.Code)
            .ToListAsync(ct);

        var rows = new List<TrialBalanceRow>();
        foreach (var account in accounts)
        {
            opening.TryGetValue(account.Id, out var open);
            period.TryGetValue(account.Id, out var move);
            var openingBalance = Money.Round(open?.Net ?? 0m);
            var debit = Money.Round(move?.Debit ?? 0m);
            var credit = Money.Round(move?.Credit ?? 0m);
            var closing = Money.Round(openingBalance + debit - credit);

            if (!includeZeroBalances && debit == 0m && credit == 0m && closing == 0m && openingBalance == 0m)
                continue;

            rows.Add(new TrialBalanceRow(account.Id, account.Code, account.Name, account.Type, account.Group,
                openingBalance, debit, credit, closing));
        }

        return new TrialBalanceReport(from, to, rows,
            Money.Round(rows.Sum(r => r.OpeningBalance)),
            Money.Round(rows.Sum(r => r.Debit)),
            Money.Round(rows.Sum(r => r.Credit)),
            Money.Round(rows.Sum(r => r.ClosingBalance)));
    }

    public async Task<ProfitAndLossReport> ProfitAndLossAsync(Guid companyId, DateOnly from, DateOnly to,
        DateOnly? compareFrom = null, DateOnly? compareTo = null, bool includeZeroBalances = false,
        CancellationToken ct = default)
    {
        if (to < from) throw new DomainValidationException("The end date must be on or after the start date.");

        var report = await BuildProfitAndLossAsync(companyId, from, to, includeZeroBalances, ct);
        if (compareFrom.HasValue && compareTo.HasValue)
        {
            var comparative = await BuildProfitAndLossAsync(companyId, compareFrom.Value, compareTo.Value,
                includeZeroBalances, ct);
            report = report with { Comparative = comparative };
        }
        return report;
    }

    private async Task<ProfitAndLossReport> BuildProfitAndLossAsync(Guid companyId, DateOnly from, DateOnly to,
        bool includeZero, CancellationToken ct)
    {
        var sums = await SumsAsync(companyId, from, to, ct);
        var accounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && (a.Type == AccountType.Revenue || a.Type == AccountType.Expense))
            .OrderBy(a => a.Code)
            .ToListAsync(ct);

        var rows = accounts
            .Select(a =>
            {
                sums.TryGetValue(a.Id, out var s);
                var amount = a.Type == AccountType.Revenue
                    ? (s?.Credit ?? 0m) - (s?.Debit ?? 0m)
                    : (s?.Debit ?? 0m) - (s?.Credit ?? 0m);
                return new { Account = a, Amount = Money.Round(amount) };
            })
            .Where(x => includeZero || x.Amount != 0m)
            .ToList();

        StatementSection Section(string title, AccountGroup group, string key) => new(
            title,
            rows.Where(r => r.Account.Group == group)
                .Select(r => new StatementRow(r.Account.Code, r.Account.Name, r.Amount, 0, false, r.Account.Id))
                .ToList(),
            Money.Round(rows.Where(r => r.Account.Group == group).Sum(r => r.Amount)),
            key);

        var revenue = Section("Revenue", AccountGroup.Revenue, "revenue");
        var costOfSales = Section("Cost of sales", AccountGroup.CostOfSales, "cogs");
        var operatingExpenses = Section("Operating expenses", AccountGroup.OperatingExpense, "opex");
        var otherIncome = Section("Other income", AccountGroup.OtherIncome, "other-income");
        var otherExpense = Section("Other expenses", AccountGroup.OtherExpense, "other-expense");

        var grossProfit = Money.Round(revenue.Total - costOfSales.Total);
        var operatingProfit = Money.Round(grossProfit - operatingExpenses.Total);
        var netProfit = Money.Round(operatingProfit + otherIncome.Total - otherExpense.Total);

        return new ProfitAndLossReport(from, to,
            new[] { revenue, costOfSales, operatingExpenses, otherIncome, otherExpense },
            revenue.Total, costOfSales.Total, grossProfit, operatingExpenses.Total, operatingProfit,
            otherIncome.Total, otherExpense.Total, netProfit);
    }

    public async Task<BalanceSheetReport> BalanceSheetAsync(Guid companyId, DateOnly asOf,
        CancellationToken ct = default)
    {
        var sums = await SumsAsync(companyId, null, asOf, ct);
        var accounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId)
            .OrderBy(a => a.Code)
            .ToListAsync(ct);

        decimal Balance(Account account)
        {
            sums.TryGetValue(account.Id, out var s);
            return account.Type switch
            {
                AccountType.Asset => Money.Round((s?.Debit ?? 0m) - (s?.Credit ?? 0m)),
                AccountType.Liability or AccountType.Equity => Money.Round((s?.Credit ?? 0m) - (s?.Debit ?? 0m)),
                _ => 0m
            };
        }

        var revenue = accounts.Where(a => a.Type == AccountType.Revenue)
            .Sum(a => (sums.TryGetValue(a.Id, out var s) ? s.Credit - s.Debit : 0m));
        var expenses = accounts.Where(a => a.Type == AccountType.Expense)
            .Sum(a => (sums.TryGetValue(a.Id, out var s) ? s.Debit - s.Credit : 0m));
        var currentEarnings = Money.Round(revenue - expenses);

        StatementSection Section(string title, AccountGroup group, string key)
        {
            var items = accounts.Where(a => a.Group == group && a.Type != AccountType.Revenue && a.Type != AccountType.Expense)
                .Select(a => new { Account = a, Amount = Balance(a) })
                .Where(x => x.Amount != 0m)
                .Select(x => new StatementRow(x.Account.Code, x.Account.Name, x.Amount, 0, false, x.Account.Id))
                .ToList();
            return new StatementSection(title, items, Money.Round(items.Sum(i => i.Amount)), key);
        }

        var currentAssets = Section("Current assets", AccountGroup.CurrentAsset, "current-assets");
        var fixedAssets = Section("Fixed assets", AccountGroup.FixedAsset, "fixed-assets");
        var otherAssets = Section("Other assets", AccountGroup.OtherAsset, "other-assets");
        var currentLiabilities = Section("Current liabilities", AccountGroup.CurrentLiability, "current-liabilities");
        var nonCurrentLiabilities = Section("Non-current liabilities", AccountGroup.NonCurrentLiability, "non-current-liabilities");

        var equityRows = accounts.Where(a => a.Type == AccountType.Equity)
            .Select(a => new StatementRow(a.Code, a.Name, Balance(a), 0, false, a.Id))
            .Where(r => r.Amount != 0m)
            .ToList();
        equityRows.Add(new StatementRow(null, "Current period earnings", currentEarnings));
        var equity = new StatementSection("Equity", equityRows, Money.Round(equityRows.Sum(r => r.Amount)), "equity");

        var totalAssets = Money.Round(currentAssets.Total + fixedAssets.Total + otherAssets.Total);
        var totalLiabilities = Money.Round(currentLiabilities.Total + nonCurrentLiabilities.Total);
        var diff = Money.Round(totalAssets - (totalLiabilities + equity.Total));

        return new BalanceSheetReport(asOf,
            new[] { currentAssets, fixedAssets, otherAssets, currentLiabilities, nonCurrentLiabilities, equity },
            totalAssets, totalLiabilities, equity.Total, currentEarnings, diff, Money.IsZero(diff));
    }

    public async Task<CashFlowReport> CashFlowAsync(Guid companyId, DateOnly start, DateOnly to,
        CancellationToken ct = default)
    {
        var lines = await (from l in _db.JournalLines
                           join j in _db.JournalEntries on l.JournalEntryId equals j.Id
                           join a in _db.Accounts on l.AccountId equals a.Id
                           where j.CompanyId == companyId && j.Status == JournalStatus.Posted
                                 && j.EntryDate >= start && j.EntryDate <= to
                           select new
                           {
                               l.JournalEntryId,
                               a.Name,
                               a.Code,
                               a.IsCashAccount,
                               a.CashFlowCategory,
                               l.Debit,
                               l.Credit
                           }).ToListAsync(ct);

        var openingRaw = await (from l in _db.JournalLines
                                join j in _db.JournalEntries on l.JournalEntryId equals j.Id
                                join a in _db.Accounts on l.AccountId equals a.Id
                                where j.CompanyId == companyId && j.Status == JournalStatus.Posted
                                      && j.EntryDate < start && a.IsCashAccount
                                select (double?)(l.Debit - l.Credit)).SumAsync(ct) ?? 0d;
        var opening = (decimal)openingRaw;

        var buckets = new Dictionary<(CashFlowCategory Category, string Account), decimal>();
        var netCash = 0m;

        foreach (var entry in lines.GroupBy(l => l.JournalEntryId))
        {
            var cash = entry.Where(l => l.IsCashAccount).Sum(l => l.Debit - l.Credit);
            if (Math.Abs(cash) < 0.005m) continue;

            netCash += cash;
            var counterparts = entry.Where(l => !l.IsCashAccount).ToList();
            if (counterparts.Count == 0) continue;

            var totalWeight = counterparts.Sum(c => Math.Abs(c.Debit - c.Credit));
            if (totalWeight <= 0m) continue;

            foreach (var counterpart in counterparts)
            {
                var category = counterpart.CashFlowCategory == CashFlowCategory.None
                    ? CashFlowCategory.Operating
                    : counterpart.CashFlowCategory;
                var amount = cash * (Math.Abs(counterpart.Debit - counterpart.Credit) / totalWeight);
                var key = (category, $"{counterpart.Code} {counterpart.Name}");
                buckets.TryGetValue(key, out var existing);
                buckets[key] = existing + amount;
            }
        }

        StatementSection Section(CashFlowCategory category, string title, string key) => new(
            title,
            buckets.Where(b => b.Key.Category == category && Math.Abs(b.Value) >= 0.005m)
                .OrderBy(b => b.Key.Account)
                .Select(b => new StatementRow(null, b.Key.Account, Money.Round(b.Value)))
                .ToList(),
            Money.Round(buckets.Where(b => b.Key.Category == category).Sum(b => b.Value)),
            key);

        var operating = Section(CashFlowCategory.Operating, "Operating activities", "operating");
        var investing = Section(CashFlowCategory.Investing, "Investing activities", "investing");
        var financing = Section(CashFlowCategory.Financing, "Financing activities", "financing");
        var openingCash = Money.Round(opening);
        var net = Money.Round(operating.Total + investing.Total + financing.Total);

        return new CashFlowReport(start, to, new[] { operating, investing, financing },
            net, openingCash, Money.Round(openingCash + net));
    }

    public Task<AgingReport> ReceivablesAgingAsync(Guid companyId, DateOnly asOf, CancellationToken ct = default) =>
        AgingAsync(companyId, asOf, true, ct);

    public Task<AgingReport> PayablesAgingAsync(Guid companyId, DateOnly asOf, CancellationToken ct = default) =>
        AgingAsync(companyId, asOf, false, ct);

    private async Task<AgingReport> AgingAsync(Guid companyId, DateOnly asOf, bool receivable,
        CancellationToken ct)
    {
        var parties = await _db.Parties.AsNoTracking()
            .Where(p => p.CompanyId == companyId)
            .ToDictionaryAsync(p => p.Id, ct);

        var buckets = new Dictionary<Guid, decimal[]>();
        var codes = new Dictionary<Guid, (string Code, string Name, string Currency)>();

        void Add(Guid partyId, DateOnly dueDate, decimal amount)
        {
            if (amount <= 0.004m) return;
            if (!buckets.TryGetValue(partyId, out var values))
            {
                values = new decimal[5];
                buckets[partyId] = values;
            }
            var days = asOf.DayNumber - dueDate.DayNumber;
            var index = days <= 0 ? 0 : days <= 30 ? 1 : days <= 60 ? 2 : days <= 90 ? 3 : 4;
            values[index] += amount;
            codes[partyId] = (parties[partyId].Code, parties[partyId].Name, parties[partyId].CurrencyCode);
        }

        if (receivable)
        {
            var invoices = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.CompanyId == companyId && i.Kind == InvoiceKind.Invoice &&
                            (i.Status == DocumentStatus.Issued || i.Status == DocumentStatus.PartiallyPaid) &&
                            i.IssueDate <= asOf)
                .ToListAsync(ct);
            foreach (var invoice in invoices)
                Add(invoice.PartyId, invoice.DueDate, Money.Round(invoice.BalanceDue * invoice.ExchangeRate));
        }
        else
        {
            var bills = await _db.Bills.AsNoTracking()
                .Where(b => b.CompanyId == companyId &&
                            (b.Status == DocumentStatus.Issued || b.Status == DocumentStatus.PartiallyPaid) &&
                            b.IssueDate <= asOf)
                .ToListAsync(ct);
            foreach (var bill in bills)
                Add(bill.PartyId, bill.DueDate, Money.Round(bill.BalanceDue * bill.ExchangeRate));
        }

        var credits = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.Status == DocumentStatus.Issued &&
                        p.Direction == (receivable ? PaymentDirection.Receipt : PaymentDirection.Disbursement) &&
                        p.PartyId != null && p.PaymentDate <= asOf)
            .Select(p => new
            {
                p.PartyId,
                p.Amount,
                p.ExchangeRate,
                Allocated = p.Allocations.Sum(a => (double?)a.Amount) ?? 0d
            })
            .ToListAsync(ct);

        var creditByParty = credits
            .GroupBy(c => c.PartyId!.Value)
            .ToDictionary(g => g.Key, g =>
                Money.Round(g.Sum(c => ((decimal)c.Amount - (decimal)c.Allocated) * c.ExchangeRate)));

        var rows = buckets.Select(kvp =>
        {
            var v = kvp.Value;
            var info = codes[kvp.Key];
            creditByParty.TryGetValue(kvp.Key, out var credit);
            return new AgingRow(kvp.Key, info.Code, info.Name, info.Currency,
                Money.Round(v[0]), Money.Round(v[1]), Money.Round(v[2]), Money.Round(v[3]), Money.Round(v[4]),
                Money.Round(v.Sum()), credit);
        }).OrderByDescending(r => r.Total).ToList();

        return new AgingReport(asOf, rows,
            Money.Round(rows.Sum(r => r.Current)), Money.Round(rows.Sum(r => r.Days1To30)),
            Money.Round(rows.Sum(r => r.Days31To60)), Money.Round(rows.Sum(r => r.Days61To90)),
            Money.Round(rows.Sum(r => r.Over90)), Money.Round(rows.Sum(r => r.Total)));
    }

    public async Task<TaxReport> TaxReportAsync(Guid companyId, DateOnly start, DateOnly to,
        CancellationToken ct = default)
    {
        var taxCodes = await _db.TaxCodes.AsNoTracking()
            .Where(t => t.CompanyId == companyId)
            .ToDictionaryAsync(t => t.Id, ct);

        var lines = await (from l in _db.JournalLines
                           join j in _db.JournalEntries on l.JournalEntryId equals j.Id
                           where j.CompanyId == companyId && j.Status == JournalStatus.Posted
                                 && j.EntryDate >= start && j.EntryDate <= to && l.TaxCodeId != null
                           select new { j.EntryDate, l.TaxCodeId, l.Debit, l.Credit, l.ForeignAmount })
            .ToListAsync(ct);

        var summary = new List<TaxSummaryRow>();
        foreach (var group in lines.GroupBy(l => l.TaxCodeId!.Value))
        {
            if (!taxCodes.TryGetValue(group.Key, out var code)) continue;
            var collected = Money.Round(group.Sum(l => l.Credit));
            var paid = Money.Round(group.Sum(l => l.Debit));
            summary.Add(new TaxSummaryRow(code.Id, code.Code, code.Name, code.Rate, code.Kind,
                collected, paid, Money.Round(collected - paid)));
        }

        var detail = lines
            .GroupBy(l => (l.EntryDate.ToString("yyyy-MM"), l.TaxCodeId!.Value))
            .Select(g =>
            {
                var code = taxCodes[g.Key.Item2];
                var taxAmount = Money.Round(g.Sum(l => Math.Abs(l.Credit - l.Debit)));
                var taxable = code.Rate > 0 ? Money.Round(taxAmount / code.Rate * 100m) : 0m;
                return new TaxDetailRow(g.Key.Item1, code.Code, code.Rate, taxable, taxAmount);
            })
            .OrderByDescending(r => r.Period).ThenBy(r => r.TaxCode)
            .ToList();

        return new TaxReport(start, to, summary.OrderBy(s => s.Code).ToList(), detail,
            Money.Round(summary.Sum(s => s.SalesTaxCollected)),
            Money.Round(summary.Sum(s => s.PurchaseTaxPaid)),
            Money.Round(summary.Sum(s => s.NetLiability)));
    }

    public async Task<GeneralLedgerReport> GeneralLedgerAsync(Guid companyId, Guid accountId, DateOnly start,
        DateOnly to, CancellationToken ct = default)
    {
        var account = await _db.Accounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Account not found.");

        var openingRaw = await (from l in _db.JournalLines
                                join j in _db.JournalEntries on l.JournalEntryId equals j.Id
                                where l.AccountId == accountId && j.Status == JournalStatus.Posted && j.EntryDate < start
                             select (double?)(l.Debit - l.Credit)).SumAsync(ct) ?? 0d;
        var opening = (decimal)openingRaw;

        var rows = await (from l in _db.JournalLines
                          join j in _db.JournalEntries on l.JournalEntryId equals j.Id
                          where l.AccountId == accountId && j.Status == JournalStatus.Posted
                                && j.EntryDate >= start && j.EntryDate <= to
                          orderby j.EntryDate, j.Number, l.LineNumber
                          select new
                          {
                              l.JournalEntryId,
                              j.Number,
                              j.EntryDate,
                              j.Memo,
                              j.Reference,
                              l.LineNumber,
                              l.Description,
                              l.Debit,
                              l.Credit,
                              l.PartyId,
                              j.Source
                          }).ToListAsync(ct);

        var partyIds = rows.Where(r => r.PartyId.HasValue).Select(r => r.PartyId!.Value).Distinct().ToList();
        var partyNames = await _db.Parties.Where(p => partyIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var running = opening;
        var ledgerRows = new List<GeneralLedgerRow>();
        foreach (var row in rows)
        {
            running += row.Debit - row.Credit;
            ledgerRows.Add(new GeneralLedgerRow(row.JournalEntryId, row.Number, row.EntryDate, row.Memo,
                row.Reference, row.Description, row.Debit, row.Credit, Money.Round(running),
                row.PartyId,
                row.PartyId.HasValue && partyNames.TryGetValue(row.PartyId.Value, out var name) ? name : null,
                row.Source));
        }

        return new GeneralLedgerReport(account.Id, account.Code, account.Name, start, to, Money.Round(opening),
            ledgerRows, Money.Round(rows.Sum(r => r.Debit)), Money.Round(rows.Sum(r => r.Credit)),
            Money.Round(running));
    }

    public async Task<PartyStatement> PartyStatementAsync(Guid companyId, Guid partyId, DateOnly from,
        DateOnly to, CancellationToken ct = default)
    {
        var party = await _db.Parties.FirstOrDefaultAsync(p => p.Id == partyId && p.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Party not found.");

        var isCustomer = party.IsCustomer;
        var invoices = await _db.SalesInvoices.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.PartyId == partyId &&
                        i.Status != DocumentStatus.Draft && i.Status != DocumentStatus.Void &&
                        i.IssueDate <= to)
            .ToListAsync(ct);
        var bills = await _db.Bills.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.PartyId == partyId &&
                        b.Status != DocumentStatus.Draft && b.Status != DocumentStatus.Void &&
                        b.IssueDate <= to)
            .ToListAsync(ct);
        var payments = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.PartyId == partyId && p.Status == DocumentStatus.Issued &&
                        p.PaymentDate <= to)
            .ToListAsync(ct);

        var entries = new List<(DateOnly Date, string Type, string Number, string? Reference, decimal Debit, decimal Credit)>();
        foreach (var invoice in invoices)
        {
            var amount = Money.Round(invoice.Total * invoice.ExchangeRate);
            var isCredit = invoice.Kind == InvoiceKind.CreditNote;
            entries.Add((invoice.IssueDate, isCredit ? "Credit note" : "Invoice", invoice.Number,
                invoice.PurchaseOrderNumber, isCredit ? 0m : amount, isCredit ? amount : 0m));
        }
        foreach (var bill in bills)
        {
            var amount = Money.Round(bill.Total * bill.ExchangeRate);
            entries.Add((bill.IssueDate, "Bill", bill.Number, bill.VendorInvoiceNumber, 0m, amount));
        }
        foreach (var payment in payments)
        {
            var amount = Money.Round(payment.Amount * payment.ExchangeRate);
            var receipt = payment.Direction == PaymentDirection.Receipt;
            entries.Add((payment.PaymentDate, receipt ? "Receipt" : "Payment", payment.Number, payment.Reference,
                receipt ? 0m : amount, receipt ? amount : 0m));
        }

        var prior = entries.Where(e => e.Date < from)
            .Sum(e => e.Debit - e.Credit);
        var inPeriod = entries.Where(e => e.Date >= from).OrderBy(e => e.Date).ThenBy(e => e.Number).ToList();

        var running = prior;
        var rows = new List<PartyStatementRow>();
        foreach (var entry in inPeriod)
        {
            running += entry.Debit - entry.Credit;
            rows.Add(new PartyStatementRow(entry.Date, entry.Type, entry.Number, entry.Reference,
                entry.Debit, entry.Credit, Money.Round(running)));
        }

        var closing = Money.Round(running);
        return new PartyStatement(party.Id, party.Name, from, to, Money.Round(prior), rows, closing,
            Money.Round(inPeriod.Sum(e => e.Debit)), Money.Round(inPeriod.Sum(e => e.Credit)));
    }

    public async Task<IReadOnlyList<AccountBalanceRow>> AccountBalancesAsync(Guid companyId, DateOnly asOf,
        AccountType? type = null, CancellationToken ct = default)
    {
        var sums = await SumsAsync(companyId, null, asOf, ct);
        var accounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && (type == null || a.Type == type))
            .OrderBy(a => a.Code).ToListAsync(ct);

        return accounts.Select(a =>
        {
            sums.TryGetValue(a.Id, out var s);
            var balance = a.Type is AccountType.Asset or AccountType.Expense
                ? (s?.Debit ?? 0m) - (s?.Credit ?? 0m)
                : (s?.Credit ?? 0m) - (s?.Debit ?? 0m);
            return new AccountBalanceRow(a.Id, a.Code, a.Name, a.Type, a.Group, Money.Round(balance));
        }).ToList();
    }

    public async Task<DashboardSnapshot> DashboardAsync(Guid companyId, CancellationToken ct = default)
    {
        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ct)
            ?? throw new NotFoundException("Company not found.");
        var today = _clock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var yearStart = new DateOnly(today.Year, 1, 1);

        var cashRaw = await (from l in _db.JournalLines
                             join j in _db.JournalEntries on l.JournalEntryId equals j.Id
                             join a in _db.Accounts on l.AccountId equals a.Id
                             where j.CompanyId == companyId && j.Status == JournalStatus.Posted &&
                                   j.EntryDate <= today && a.IsCashAccount
                             select (double?)(l.Debit - l.Credit)).SumAsync(ct) ?? 0d;
        var cash = (decimal)cashRaw;

        var openInvoices = await _db.SalesInvoices.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.Kind == InvoiceKind.Invoice &&
                        (i.Status == DocumentStatus.Issued || i.Status == DocumentStatus.PartiallyPaid))
            .ToListAsync(ct);
        var openBills = await _db.Bills.AsNoTracking()
            .Where(b => b.CompanyId == companyId &&
                        (b.Status == DocumentStatus.Issued || b.Status == DocumentStatus.PartiallyPaid))
            .ToListAsync(ct);

        var receivable = Money.Round(openInvoices.Sum(i => i.BalanceDue * i.ExchangeRate));
        var payable = Money.Round(openBills.Sum(b => b.BalanceDue * b.ExchangeRate));
        var overdue = openInvoices.Where(i => i.DueDate < today).ToList();

        var pnlMtd = await BuildProfitAndLossAsync(companyId, monthStart, today, false, ct);
        var pnlYtd = await BuildProfitAndLossAsync(companyId, yearStart, today, false, ct);

        var taxAccountIds = await _db.TaxCodes.Where(t => t.CompanyId == companyId)
            .Select(t => t.TaxAccountId).Distinct().ToListAsync(ct);
        var vatRaw = await (from l in _db.JournalLines
                            join j in _db.JournalEntries on l.JournalEntryId equals j.Id
                            where j.CompanyId == companyId && j.Status == JournalStatus.Posted &&
                                  j.EntryDate <= today && taxAccountIds.Contains(l.AccountId)
                            select (double?)(l.Credit - l.Debit)).SumAsync(ct) ?? 0d;
        var vat = (decimal)vatRaw;

        var draftCount = await _db.JournalEntries.CountAsync(j =>
            j.CompanyId == companyId && j.Status == JournalStatus.Draft, ct);
        var pendingCount = await _db.JournalEntries.CountAsync(j =>
            j.CompanyId == companyId && j.Status == JournalStatus.PendingApproval, ct);
        var unreconciled = await _db.BankStatementLines.CountAsync(l =>
            l.CompanyId == companyId && l.Status == BankLineStatus.Unmatched, ct);

        var since = monthStart.AddMonths(-11);
        var flowRows = await (from l in _db.JournalLines
                              join j in _db.JournalEntries on l.JournalEntryId equals j.Id
                              join a in _db.Accounts on l.AccountId equals a.Id
                              where j.CompanyId == companyId && j.Status == JournalStatus.Posted &&
                                    j.EntryDate >= since && j.EntryDate <= today &&
                                    (a.Type == AccountType.Revenue || a.Type == AccountType.Expense)
                              select new { j.EntryDate, a.Type, l.Debit, l.Credit }).ToListAsync(ct);

        var series = new List<MonthlyPoint>();
        for (var i = 0; i < 12; i++)
        {
            var month = since.AddMonths(i);
            var rows = flowRows.Where(r => r.EntryDate.Year == month.Year && r.EntryDate.Month == month.Month).ToList();
            var revenue = rows.Where(r => r.Type == AccountType.Revenue).Sum(r => r.Credit - r.Debit);
            var expenses = rows.Where(r => r.Type == AccountType.Expense).Sum(r => r.Debit - r.Credit);
            series.Add(new MonthlyPoint(month.Year, month.Month, Money.Round(revenue), Money.Round(expenses)));
        }

        var recent = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && j.Status == JournalStatus.Posted)
            .OrderByDescending(j => j.EntryDate).ThenByDescending(j => j.PostedAt)
            .Take(8)
            .Select(j => new RecentActivity(j.Id, j.Number, j.EntryDate, j.Memo, j.TotalDebit, j.Source, j.Status))
            .ToListAsync(ct);

        var invoiceRows = await (from i in _db.SalesInvoices.AsNoTracking()
                                 join p in _db.Parties on i.PartyId equals p.Id
                                 where i.CompanyId == companyId &&
                                       (i.Status == DocumentStatus.Issued || i.Status == DocumentStatus.PartiallyPaid)
                                 orderby i.DueDate
                                 select new { Invoice = i, PartyName = p.Name }).Take(6).ToListAsync(ct);

        var recentInvoices = invoiceRows.Select(x => new DashboardInvoice(x.Invoice.Id, x.Invoice.Number,
            x.PartyName, x.Invoice.DueDate, x.Invoice.BalanceDue, x.Invoice.Status,
            x.Invoice.DueDate < today)).ToList();

        return new DashboardSnapshot(company.Name, company.BaseCurrencyCode, today,
            Money.Round(cash), receivable, payable,
            pnlMtd.Revenue, pnlYtd.Revenue, pnlMtd.ExpensesTotal(), pnlYtd.ExpensesTotal(),
            pnlYtd.NetProfit, Money.Round(vat), overdue.Count,
            Money.Round(overdue.Sum(i => i.BalanceDue * i.ExchangeRate)),
            draftCount, pendingCount, unreconciled, series, recent, recentInvoices);
    }
}

internal static class ProfitAndLossExtensions
{
    public static decimal ExpensesTotal(this ProfitAndLossReport report) =>
        Money.Round(report.CostOfSales + report.OperatingExpenses + report.OtherExpense);
}
