using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AxiomLedger.Application.Models;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

/// <summary>
/// Bank feeds, statement imports, matching and reconciliation. Statement lines are never posted directly;
/// they are matched to posted ledger lines (or turned into a journal for fees and interest).
/// </summary>
public sealed class BankingService
{
    private readonly IAccountingDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly IJournalPoster _poster;

    public BankingService(IAccountingDbContext db, ICurrentUser user, IClock clock, AuditService audit,
        IJournalPoster poster)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _audit = audit;
        _poster = poster;
    }

    public async Task<IReadOnlyList<BankAccountListItem>> ListAccountsAsync(Guid companyId,
        CancellationToken ct = default)
    {
        var accounts = await _db.BankAccounts.AsNoTracking()
            .Where(b => b.CompanyId == companyId)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
        var glIds = accounts.Select(a => a.AccountId).Distinct().ToList();
        var ledgerAccounts = await _db.Accounts.AsNoTracking()
            .Where(a => glIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        var sums = await (from l in _db.JournalLines
                          join j in _db.JournalEntries on l.JournalEntryId equals j.Id
                          where j.CompanyId == companyId && j.Status == JournalStatus.Posted && glIds.Contains(l.AccountId)
                          group l by l.AccountId into g
                          select new { AccountId = g.Key, Balance = g.Sum(x => (double)(x.Debit - x.Credit)) })
            .ToDictionaryAsync(x => x.AccountId, x => (decimal)x.Balance, ct);

        var unreconciled = await _db.BankStatementLines
            .Where(l => l.CompanyId == companyId && l.Status == BankLineStatus.Unmatched)
            .GroupBy(l => l.BankAccountId)
            .Select(g => new { BankAccountId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.BankAccountId, x => x.Count, ct);

        return accounts.Select(b =>
        {
            ledgerAccounts.TryGetValue(b.AccountId, out var ledger);
            sums.TryGetValue(b.AccountId, out var balance);
            unreconciled.TryGetValue(b.Id, out var count);
            return new BankAccountListItem(b.Id, b.Name, b.BankName, b.AccountNumber, b.AccountId,
                ledger?.Code ?? string.Empty, ledger?.Name ?? string.Empty, b.CurrencyCode,
                Money.Round(balance), count, b.IsActive, b.LastReconciledDate);
        }).ToList();
    }

    public async Task<BankAccount> CreateAccountAsync(Guid companyId, BankAccountRequest request,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageBanking);
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new DomainValidationException("Give the bank account a name.");

        using var companyLock = await CompanyLock.AcquireAsync(companyId, ct);

        Account? ledger;
        if (request.AccountId is { } accountId)
        {
            ledger = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId, ct)
                ?? throw new DomainValidationException("Select a valid ledger account.");
        }
        else if (request.CreateLedgerAccount)
        {
            var nextCode = await NextAssetCodeAsync(companyId, ct);
            ledger = new Account
            {
                CompanyId = companyId,
                Code = nextCode,
                Name = request.Name.Trim() + " (bank)",
                Type = AccountType.Asset,
                Group = AccountGroup.CurrentAsset,
                IsCashAccount = true,
                IsPostable = true,
                CashFlowCategory = CashFlowCategory.Operating,
                CurrencyCode = request.CurrencyCode,
                CreatedAt = _clock.UtcNow
            };
            _db.Accounts.Add(ledger);
        }
        else
        {
            throw new DomainValidationException("Select the ledger account for this bank account.");
        }

        var bank = new BankAccount
        {
            CompanyId = companyId,
            Name = request.Name.Trim(),
            BankName = request.BankName,
            AccountNumber = request.AccountNumber,
            Iban = request.Iban,
            Swift = request.Swift,
            AccountId = ledger.Id,
            CurrencyCode = request.CurrencyCode.ToUpperInvariant(),
            IsActive = true,
            CreatedAt = _clock.UtcNow,
            CreatedByUserId = _user.UserId
        };
        _db.BankAccounts.Add(bank);
        _audit.Record(companyId, "BankAccountCreated", nameof(BankAccount), bank.Id.ToString(), bank.Name);
        await _db.SaveChangesAsync(ct);
        return bank;
    }

    private async Task<string> NextAssetCodeAsync(Guid companyId, CancellationToken ct)
    {
        var codes = await _db.Accounts.Where(a => a.CompanyId == companyId && a.Type == AccountType.Asset)
            .Select(a => a.Code).ToListAsync(ct);
        var next = 1050;
        while (codes.Contains(next.ToString(CultureInfo.InvariantCulture))) next += 10;
        return next.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<StatementImportResult> ImportCsvAsync(Guid companyId, Guid bankAccountId, string csv,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageBanking);
        if (string.IsNullOrWhiteSpace(csv))
            throw new DomainValidationException("The file is empty.");

        var bank = await _db.BankAccounts.FirstOrDefaultAsync(b => b.Id == bankAccountId && b.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Bank account not found.");

        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var rows = Csv.Parse(csv);
        if (rows.Count < 2)
            throw new DomainValidationException("The statement needs a header row and at least one transaction.");

        var headers = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Index(params string[] names) => headers.FindIndex(h => names.Any(n => h == n || h.Contains(n)));

        var dateIdx = Index("date");
        var descIdx = Index("description", "details", "narrative", "memo", "payee");
        var amountIdx = Index("amount");
        var debitIdx = Index("debit", "money out", "withdrawal", "paid out");
        var creditIdx = Index("credit", "money in", "deposit", "paid in");
        var balanceIdx = Index("balance");
        var referenceIdx = Index("reference", "ref", "transaction id");

        if (dateIdx < 0 || (amountIdx < 0 && debitIdx < 0 && creditIdx < 0))
            throw new DomainValidationException(
                "The statement must contain a date column and either an amount or debit/credit columns.");

        var existing = await _db.BankStatementLines
            .Where(l => l.BankAccountId == bankAccountId && l.ExternalId != null)
            .Select(l => l.ExternalId!)
            .ToListAsync(ct);
        var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var batchId = Guid.NewGuid();
        var imported = 0;
        var duplicates = 0;
        var skipped = 0;
        var errors = new List<string>();

        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            string Cell(int index) => index >= 0 && index < row.Count ? row[index].Trim() : string.Empty;

            if (!Csv.TryDate(Cell(dateIdx), out var date))
            {
                skipped++;
                if (errors.Count < 10) errors.Add($"Row {rowIndex + 1}: '{Cell(dateIdx)}' is not a recognised date.");
                continue;
            }

            decimal debit = 0m, credit = 0m;
            if (amountIdx >= 0 && !string.IsNullOrWhiteSpace(Cell(amountIdx)))
            {
                if (!Csv.TryAmount(Cell(amountIdx), out var amount))
                {
                    skipped++;
                    if (errors.Count < 10) errors.Add($"Row {rowIndex + 1}: '{Cell(amountIdx)}' is not a valid amount.");
                    continue;
                }
                if (amount >= 0) credit = amount; else debit = -amount;
            }
            else
            {
                if (debitIdx >= 0 && !string.IsNullOrWhiteSpace(Cell(debitIdx)))
                    Csv.TryAmount(Cell(debitIdx), out debit);
                if (creditIdx >= 0 && !string.IsNullOrWhiteSpace(Cell(creditIdx)))
                    Csv.TryAmount(Cell(creditIdx), out credit);
            }

            if (debit == 0m && credit == 0m)
            {
                skipped++;
                continue;
            }

            decimal? balance = null;
            if (balanceIdx >= 0 && Csv.TryAmount(Cell(balanceIdx), out var parsedBalance)) balance = parsedBalance;

            var description = Cell(descIdx);
            var reference = Cell(referenceIdx);
            var externalId = Hash($"{bankAccountId}|{date:yyyy-MM-dd}|{debit:0.00}|{credit:0.00}|{description}|{reference}");
            if (!seen.Add(externalId))
            {
                duplicates++;
                continue;
            }

            _db.BankStatementLines.Add(new BankStatementLine
            {
                CompanyId = companyId,
                BankAccountId = bankAccountId,
                ImportBatchId = batchId,
                TransactionDate = date,
                Description = description,
                Reference = reference,
                Debit = Money.Round(debit),
                Credit = Money.Round(credit),
                Balance = balance,
                Status = BankLineStatus.Unmatched,
                ExternalId = externalId,
                ImportedAt = _clock.UtcNow
            });
            imported++;
        }

        _audit.Record(companyId, "StatementImported", nameof(BankStatementLine), batchId.ToString(),
            $"{imported} imported, {duplicates} duplicates, {skipped} skipped");
        await _db.SaveChangesAsync(ct);

        return new StatementImportResult(imported, duplicates, skipped, errors);
    }

    private static string Hash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    public async Task<IReadOnlyList<BankTransactionView>> ListLinesAsync(Guid companyId, Guid bankAccountId,
        BankLineStatus? status = null, DateOnly? from = null, DateOnly? to = null,
        CancellationToken ct = default)
    {
        var query = _db.BankStatementLines.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.BankAccountId == bankAccountId);

        if (status.HasValue) query = query.Where(l => l.Status == status);
        if (from.HasValue) query = query.Where(l => l.TransactionDate >= from);
        if (to.HasValue) query = query.Where(l => l.TransactionDate <= to);

        var lines = await query.OrderByDescending(l => l.TransactionDate).ThenBy(l => l.Id).ToListAsync(ct);
        var matchedIds = lines.Where(l => l.MatchedJournalLineId.HasValue)
            .Select(l => l.MatchedJournalLineId!.Value).Distinct().ToList();
        var numbers = await (from jl in _db.JournalLines
                             join j in _db.JournalEntries on jl.JournalEntryId equals j.Id
                             where matchedIds.Contains(jl.Id)
                             select new { jl.Id, j.Number })
            .ToDictionaryAsync(x => x.Id, x => x.Number, ct);

        return lines.Select(l => new BankTransactionView(l.Id, l.TransactionDate, l.Description, l.Reference,
            l.Debit, l.Credit, l.Balance, l.Status, l.MatchedJournalLineId,
            l.MatchedJournalLineId.HasValue && numbers.TryGetValue(l.MatchedJournalLineId.Value, out var n) ? n : null,
            l.ImportedAt)).ToList();
    }

    public async Task<IReadOnlyList<MatchingCandidate>> GetCandidatesAsync(Guid companyId, Guid statementLineId,
        CancellationToken ct = default)
    {
        var line = await _db.BankStatementLines
            .FirstOrDefaultAsync(l => l.Id == statementLineId && l.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Statement line not found.");
        var bank = await _db.BankAccounts.FirstOrDefaultAsync(b => b.Id == line.BankAccountId, ct)
            ?? throw new NotFoundException("Bank account not found.");

        var min = line.TransactionDate.AddDays(-14);
        var max = line.TransactionDate.AddDays(14);
        var amount = line.Credit - line.Debit;

        // Math.Abs is not translatable by every provider (SQLite), so the amount tolerance is applied in memory
        // over the narrow 28-day window.
        var window = await (from jl in _db.JournalLines
                            join j in _db.JournalEntries on jl.JournalEntryId equals j.Id
                            where j.CompanyId == companyId && j.Status == JournalStatus.Posted
                                  && jl.AccountId == bank.AccountId && !jl.IsCleared
                                  && j.EntryDate >= min && j.EntryDate <= max
                            orderby j.EntryDate
                            select new
                            {
                                jl.Id,
                                j.Number,
                                j.EntryDate,
                                j.Memo,
                                Amount = jl.Debit - jl.Credit,
                                j.Reference
                            }).ToListAsync(ct);

        return window
            .Where(c => Math.Abs(c.Amount - amount) < 0.005m)
            .Take(50)
            .Select(c => new MatchingCandidate(c.Id, c.Number, c.EntryDate, c.Memo, c.Amount, bank.Name, c.Reference))
            .ToList();
    }

    public async Task<int> AutoMatchAsync(Guid companyId, Guid bankAccountId, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageBanking);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var lines = await _db.BankStatementLines
            .Where(l => l.CompanyId == companyId && l.BankAccountId == bankAccountId &&
                        l.Status == BankLineStatus.Unmatched)
            .OrderBy(l => l.TransactionDate)
            .ToListAsync(ct);

        var matched = 0;
        foreach (var line in lines)
        {
            var candidates = await GetCandidatesAsync(companyId, line.Id, ct);
            if (candidates.Count != 1) continue;
            var journalLine = await _db.JournalLines.FirstOrDefaultAsync(j => j.Id == candidates[0].JournalLineId, ct);
            if (journalLine is null || journalLine.IsCleared) continue;

            journalLine.IsCleared = true;
            journalLine.BankStatementLineId = line.Id;
            line.Status = BankLineStatus.Matched;
            line.MatchedJournalLineId = journalLine.Id;
            matched++;
        }

        _audit.Record(companyId, "BankAutoMatched", nameof(BankAccount), bankAccountId.ToString(),
            $"{matched} line(s) matched");
        await _db.SaveChangesAsync(ct);
        return matched;
    }

    public async Task MatchAsync(Guid companyId, Guid statementLineId, Guid journalLineId,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageBanking);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var line = await _db.BankStatementLines
            .FirstOrDefaultAsync(l => l.Id == statementLineId && l.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Statement line not found.");
        if (line.Status == BankLineStatus.Matched)
            throw new DomainValidationException("This statement line is already matched. Unmatch it first.");

        var bank = await _db.BankAccounts.FirstOrDefaultAsync(b => b.Id == line.BankAccountId, ct)
            ?? throw new NotFoundException("Bank account not found.");
        var journalLine = await _db.JournalLines.Include(j => j.JournalEntry)
            .FirstOrDefaultAsync(j => j.Id == journalLineId && j.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Journal line not found.");

        if (journalLine.AccountId != bank.AccountId)
            throw new DomainValidationException("That journal line is not on this bank's ledger account.");
        if (journalLine.JournalEntry?.Status != JournalStatus.Posted)
            throw new DomainValidationException("Only posted ledger lines can be matched.");
        if (journalLine.IsCleared)
            throw new DomainValidationException("That ledger line is already reconciled.");
        if (Math.Abs((journalLine.Debit - journalLine.Credit) - (line.Credit - line.Debit)) > 0.005m)
            throw new DomainValidationException("The amounts do not match.");

        journalLine.IsCleared = true;
        journalLine.BankStatementLineId = line.Id;
        line.Status = BankLineStatus.Matched;
        line.MatchedJournalLineId = journalLine.Id;

        _audit.Record(companyId, "BankLineMatched", nameof(BankStatementLine), line.Id.ToString(),
            $"Matched to journal line {journalLineId}");
        await _db.SaveChangesAsync(ct);
    }

    public async Task UnmatchAsync(Guid companyId, Guid statementLineId, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageBanking);
        var line = await _db.BankStatementLines
            .FirstOrDefaultAsync(l => l.Id == statementLineId && l.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Statement line not found.");

        if (line.MatchedJournalLineId is { } journalLineId)
        {
            var journalLine = await _db.JournalLines.FirstOrDefaultAsync(j => j.Id == journalLineId, ct);
            if (journalLine is not null)
            {
                journalLine.IsCleared = false;
                journalLine.BankStatementLineId = null;
            }
        }

        line.Status = BankLineStatus.Unmatched;
        line.MatchedJournalLineId = null;
        line.MatchedPaymentId = null;

        _audit.Record(companyId, "BankLineUnmatched", nameof(BankStatementLine), line.Id.ToString());
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetExcludedAsync(Guid companyId, Guid statementLineId, bool excluded,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageBanking);
        var line = await _db.BankStatementLines
            .FirstOrDefaultAsync(l => l.Id == statementLineId && l.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Statement line not found.");
        if (line.Status == BankLineStatus.Matched)
            throw new DomainValidationException("Unmatch the line before excluding it.");

        line.Status = excluded ? BankLineStatus.Excluded : BankLineStatus.Unmatched;
        _audit.Record(companyId, excluded ? "BankLineExcluded" : "BankLineIncluded",
            nameof(BankStatementLine), line.Id.ToString());
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Creates and posts a journal for an unmatched statement line (bank fees, interest, transfers).</summary>
    public async Task<Guid> CreateEntryFromLineAsync(Guid companyId, Guid statementLineId, Guid contraAccountId,
        string? description, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageBanking);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var line = await _db.BankStatementLines
            .FirstOrDefaultAsync(l => l.Id == statementLineId && l.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Statement line not found.");
        if (line.Status == BankLineStatus.Matched)
            throw new DomainValidationException("This line is already matched.");

        var bank = await _db.BankAccounts.FirstOrDefaultAsync(b => b.Id == line.BankAccountId, ct)
            ?? throw new NotFoundException("Bank account not found.");
        var contra = await _db.Accounts
            .FirstOrDefaultAsync(a => a.Id == contraAccountId && a.CompanyId == companyId && a.IsPostable, ct)
            ?? throw new DomainValidationException("Select a valid posting account.");

        var moneyIn = line.Credit > 0m;
        var amount = moneyIn ? line.Credit : line.Debit;

        var entry = new JournalEntry
        {
            CompanyId = companyId,
            EntryDate = line.TransactionDate,
            Reference = line.Reference,
            Memo = description ?? line.Description ?? "Bank statement entry",
            Source = JournalSource.BankImport,
            CreatedByUserId = _user.UserId,
            CreatedAt = _clock.UtcNow
        };
        entry.Lines.Add(new JournalLine
        {
            CompanyId = companyId,
            AccountId = bank.AccountId,
            Description = entry.Memo,
            Debit = moneyIn ? amount : 0m,
            Credit = moneyIn ? 0m : amount,
            LineNumber = 1
        });
        entry.Lines.Add(new JournalLine
        {
            CompanyId = companyId,
            AccountId = contra.Id,
            Description = entry.Memo,
            Debit = moneyIn ? 0m : amount,
            Credit = moneyIn ? amount : 0m,
            LineNumber = 2
        });

        await _poster.PostAsync(entry, ct);
        _db.JournalEntries.Add(entry);

        line.Status = BankLineStatus.Matched;
        line.MatchedJournalLineId = entry.Lines[0].Id;
        entry.Lines[0].IsCleared = true;
        entry.Lines[0].BankStatementLineId = line.Id;

        _audit.Record(companyId, "BankEntryCreated", nameof(JournalEntry), entry.Id.ToString(), entry.Memo);
        await _db.SaveChangesAsync(ct);
        return entry.Id;
    }

    public async Task<ReconciliationSummary> GetReconciliationAsync(Guid companyId, Guid bankAccountId,
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var bank = await _db.BankAccounts.FirstOrDefaultAsync(b => b.Id == bankAccountId && b.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Bank account not found.");

        var ledgerBalanceRaw = await (from l in _db.JournalLines
                                   join j in _db.JournalEntries on l.JournalEntryId equals j.Id
                                   where j.CompanyId == companyId && j.Status == JournalStatus.Posted
                                         && l.AccountId == bank.AccountId && j.EntryDate <= to
                                   select (double?)(l.Debit - l.Credit)).SumAsync(ct) ?? 0d;
        var ledgerBalance = (decimal)ledgerBalanceRaw;

        var statementLines = await _db.BankStatementLines.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.BankAccountId == bankAccountId &&
                        l.TransactionDate >= from && l.TransactionDate <= to)
            .OrderBy(l => l.TransactionDate)
            .ToListAsync(ct);

        var statementToDate = await _db.BankStatementLines.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.BankAccountId == bankAccountId && l.TransactionDate <= to)
            .OrderBy(l => l.TransactionDate).ThenBy(l => l.ImportedAt)
            .ToListAsync(ct);

        var statementBalance = statementToDate.LastOrDefault(l => l.Balance.HasValue)?.Balance
                               ?? statementToDate.Sum(l => l.SignedAmount);

        var unmatched = statementLines.Where(l => l.Status == BankLineStatus.Unmatched).ToList();
        var outstandingOut = unmatched.Where(l => l.Debit > 0).Sum(l => l.Debit);
        var outstandingIn = unmatched.Where(l => l.Credit > 0).Sum(l => l.Credit);

        var matchedNumbers = await (from l in _db.BankStatementLines
                                    join jl in _db.JournalLines on l.MatchedJournalLineId equals jl.Id
                                    join j in _db.JournalEntries on jl.JournalEntryId equals j.Id
                                    where l.CompanyId == companyId && l.BankAccountId == bankAccountId
                                    select new { l.Id, j.Number })
            .ToDictionaryAsync(x => x.Id, x => x.Number, ct);

        var rows = statementLines.Select(l => new ReconciliationLine(l.Id, l.TransactionDate, l.Description,
            l.SignedAmount, l.Status,
            l.MatchedJournalLineId is { } id && matchedNumbers.TryGetValue(l.Id, out var n) ? n : null)).ToList();

        return new ReconciliationSummary(bank.Id, bank.Name, from, to,
            Money.Round(statementToDate.FirstOrDefault()?.Balance ?? 0m),
            Money.Round(statementBalance), Money.Round(ledgerBalance),
            Money.Round(outstandingOut), Money.Round(outstandingIn), unmatched.Count, rows);
    }

    public async Task CompleteReconciliationAsync(Guid companyId, Guid bankAccountId, DateOnly from, DateOnly to,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageBanking);
        using var _ = await CompanyLock.AcquireAsync(companyId, ct);

        var summary = await GetReconciliationAsync(companyId, bankAccountId, from, to, ct);
        if (!summary.IsBalanced)
            throw new DomainValidationException(
                $"The reconciliation does not balance. Difference: {summary.Difference:N2}. " +
                "Match the outstanding statement lines or investigate the ledger.");

        _db.BankReconciliations.Add(new BankReconciliation
        {
            CompanyId = companyId,
            BankAccountId = bankAccountId,
            PeriodStart = from,
            PeriodEnd = to,
            StatementClosingBalance = summary.StatementBalance,
            LedgerClosingBalance = summary.LedgerBalance,
            OutstandingChecks = summary.OutstandingChecks,
            DepositsInTransit = summary.DepositsInTransit,
            Status = ReconciliationStatus.Completed,
            CompletedAt = _clock.UtcNow,
            CompletedByUserId = _user.UserId
        });

        var bank = await _db.BankAccounts.FirstAsync(b => b.Id == bankAccountId, ct);
        bank.LastReconciledDate = to;

        _audit.Record(companyId, "ReconciliationCompleted", nameof(BankAccount), bankAccountId.ToString(),
            $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}");
        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>Small, dependency-free CSV parser (RFC 4180 quoting) plus amount/date coercion.</summary>
internal static class Csv
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else
            {
                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        break;
                    case ',':
                        row.Add(field.ToString());
                        field.Clear();
                        break;
                    case '\r':
                        break;
                    case '\n':
                        row.Add(field.ToString());
                        field.Clear();
                        if (row.Count > 1 || row[0].Trim().Length > 0)
                            rows.Add(row);
                        row = new List<string>();
                        break;
                    default:
                        field.Append(c);
                        break;
                }
            }
        }

        row.Add(field.ToString());
        if (row.Count > 1 || row[0].Trim().Length > 0) rows.Add(row);
        return rows;
    }

    public static bool TryDate(string value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var formats = new[]
        {
            "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd", "yyyyMMdd",
            "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy",
            "MM/dd/yyyy", "MM-dd-yyyy",
            "dd MMM yyyy", "MMM dd, yyyy", "dd-MMM-yyyy"
        };
        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed) ||
            DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            date = DateOnly.FromDateTime(parsed);
            return true;
        }
        return false;
    }

    public static bool TryAmount(string value, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        var negative = (text.StartsWith('(') && text.EndsWith(')')) || text.StartsWith('-');
        text = text.Trim('(', ')', ' ');
        text = new string(text.Where(c => char.IsDigit(c) || c == '.' || c == ',' || c == '-').ToArray());

        if (text.Contains(',') && text.Contains('.'))
        {
            text = text.Replace(",", string.Empty);
        }
        else if (text.Contains(','))
        {
            var parts = text.Split(',');
            text = parts[^1].Length == 2 ? text.Replace(",", ".") : text.Replace(",", string.Empty);
        }

        text = text.Replace("-", string.Empty);
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
            return false;

        if (negative) amount = -amount;
        return true;
    }
}
