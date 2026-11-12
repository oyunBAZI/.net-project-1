using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

/// <summary>
/// Resolves the control accounts used by automated postings. Configured ids win; well-known chart codes are
/// the fallback so companies that migrate their own chart keep working.
/// </summary>
public sealed class LedgerAccountResolver
{
    private readonly IAccountingDbContext _db;

    public LedgerAccountResolver(IAccountingDbContext db) => _db = db;

    public Task<Account> ReceivableAsync(Guid companyId, CancellationToken ct = default) =>
        ResolveAsync(companyId, s => s.AccountsReceivableAccountId, "1100", AccountType.Asset, ct);

    public Task<Account> PayableAsync(Guid companyId, CancellationToken ct = default) =>
        ResolveAsync(companyId, s => s.AccountsPayableAccountId, "2000", AccountType.Liability, ct);

    public Task<Account> RetainedEarningsAsync(Guid companyId, CancellationToken ct = default) =>
        ResolveAsync(companyId, s => s.RetainedEarningsAccountId, "3100", AccountType.Equity, ct);

    public async Task<Account> FxGainAsync(Guid companyId, CancellationToken ct = default) =>
        await ByCodeAsync(companyId, "4950", ct)
        ?? await _db.Accounts.Where(a => a.CompanyId == companyId && a.Group == AccountGroup.OtherIncome && a.IsActive)
            .OrderBy(a => a.Code).FirstOrDefaultAsync(ct)
        ?? throw new DomainValidationException(
            "No foreign-exchange gain account is configured. Add one in the chart of accounts.");

    public async Task<Account> FxLossAsync(Guid companyId, CancellationToken ct = default) =>
        await ByCodeAsync(companyId, "8200", ct)
        ?? await _db.Accounts.Where(a => a.CompanyId == companyId && a.Group == AccountGroup.OtherExpense && a.IsActive)
            .OrderBy(a => a.Code).FirstOrDefaultAsync(ct)
        ?? throw new DomainValidationException(
            "No foreign-exchange loss account is configured. Add one in the chart of accounts.");

    public async Task<Account?> ByCodeAsync(Guid companyId, string code, CancellationToken ct = default) =>
        await _db.Accounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.Code == code, ct);

    private async Task<Account> ResolveAsync(Guid companyId, Func<CompanySettings, Guid?> selector,
        string fallbackCode, AccountType expectedType, CancellationToken ct)
    {
        var settings = await _db.CompanySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);

        if (settings is not null && selector(settings) is { } id)
        {
            var configured = await _db.Accounts
                .FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == companyId, ct);
            if (configured is not null) return configured;
        }

        var account = await ByCodeAsync(companyId, fallbackCode, ct)
            ?? await _db.Accounts.Where(a => a.CompanyId == companyId && a.Type == expectedType && a.IsPostable && a.IsActive)
                .OrderBy(a => a.Code).FirstOrDefaultAsync(ct);

        return account ?? throw new DomainValidationException(
            $"No {expectedType} control account is configured. Add one under Settings \u2192 Chart of accounts.");
    }
}
