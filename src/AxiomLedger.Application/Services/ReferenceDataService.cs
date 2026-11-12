using AxiomLedger.Application.Models;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

/// <summary>Reference data lookups shared by the UI, API and importers.</summary>
public sealed class ReferenceDataService
{
    private readonly IAccountingDbContext _db;

    public ReferenceDataService(IAccountingDbContext db) => _db = db;

    public async Task<IReadOnlyList<AccountOption>> GetPostableAccountsAsync(Guid companyId,
        CancellationToken ct = default) =>
        await _db.Accounts
            .Where(a => a.CompanyId == companyId && a.IsPostable && a.IsActive)
            .OrderBy(a => a.Code)
            .Select(a => new AccountOption(a.Id, a.Code, a.Name, a.Type, a.Group))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AccountOption>> GetAllAccountsAsync(Guid companyId,
        CancellationToken ct = default) =>
        await _db.Accounts
            .Where(a => a.CompanyId == companyId)
            .OrderBy(a => a.Code)
            .Select(a => new AccountOption(a.Id, a.Code, a.Name, a.Type, a.Group))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AccountOption>> GetAccountsByTypeAsync(Guid companyId, AccountType type,
        CancellationToken ct = default) =>
        await _db.Accounts
            .Where(a => a.CompanyId == companyId && a.Type == type && a.IsPostable && a.IsActive)
            .OrderBy(a => a.Code)
            .Select(a => new AccountOption(a.Id, a.Code, a.Name, a.Type, a.Group))
            .ToListAsync(ct);

    public async Task<Account?> GetAccountAsync(Guid companyId, Guid accountId, CancellationToken ct = default) =>
        await _db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId, ct);

    public async Task<IReadOnlyList<PartyOption>> GetPartiesAsync(Guid companyId, PartyType? type = null,
        CancellationToken ct = default)
    {
        var query = _db.Parties.Where(p => p.CompanyId == companyId && p.IsActive);
        if (type == PartyType.Customer)
            query = query.Where(p => p.Type == PartyType.Customer || p.Type == PartyType.Both);
        else if (type == PartyType.Vendor)
            query = query.Where(p => p.Type == PartyType.Vendor || p.Type == PartyType.Both);

        return await query.OrderBy(p => p.Name)
            .Select(p => new PartyOption(p.Id, p.Name, p.Type))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TaxCode>> GetTaxCodesAsync(Guid companyId, TaxKind? kind = null,
        CancellationToken ct = default)
    {
        var query = _db.TaxCodes.Where(t => t.CompanyId == companyId && t.IsActive);
        if (kind == TaxKind.Sales) query = query.Where(t => t.Kind == TaxKind.Sales || t.Kind == TaxKind.Both);
        if (kind == TaxKind.Purchase) query = query.Where(t => t.Kind == TaxKind.Purchase || t.Kind == TaxKind.Both);
        // SQLite cannot ORDER BY a decimal column; the rate is a display concern, so order by code.
        return await query.OrderBy(t => t.Code).ToListAsync(ct);
    }

    public async Task<TaxCode?> GetTaxCodeAsync(Guid companyId, Guid? id, CancellationToken ct = default) =>
        id is null ? null : await _db.TaxCodes.FirstOrDefaultAsync(t => t.Id == id && t.CompanyId == companyId, ct);

    public async Task<IReadOnlyList<Department>> GetDepartmentsAsync(Guid companyId,
        CancellationToken ct = default) =>
        await _db.Departments.Where(d => d.CompanyId == companyId && d.IsActive)
            .OrderBy(d => d.Code).ToListAsync(ct);

    public async Task<IReadOnlyList<Currency>> GetCurrenciesAsync(CancellationToken ct = default) =>
        await _db.Currencies.Where(c => c.IsActive).OrderBy(c => c.Code).ToListAsync(ct);

    public async Task<decimal> ResolveExchangeRateAsync(Guid companyId, string? currencyCode, DateOnly date,
        CancellationToken ct = default)
    {
        var baseCurrency = await _db.Companies.Where(c => c.Id == companyId)
            .Select(c => c.BaseCurrencyCode).FirstOrDefaultAsync(ct) ?? "USD";

        if (string.IsNullOrWhiteSpace(currencyCode) ||
            currencyCode.Trim().Equals(baseCurrency, StringComparison.OrdinalIgnoreCase))
            return 1m;

        var currency = currencyCode.Trim().ToUpperInvariant();
        var rate = await _db.ExchangeRates
            .Where(r => r.CompanyId == companyId && r.CurrencyCode == currency && r.RateDate <= date)
            .OrderByDescending(r => r.RateDate)
            .Select(r => (decimal?)r.Rate)
            .FirstOrDefaultAsync(ct);

        if (rate is null or <= 0)
            throw new DomainValidationException(
                $"No exchange rate is defined for {currency} on or before {date:yyyy-MM-dd}. Add one under Settings \u2192 Currencies.");

        return rate.Value;
    }

    public async Task<IReadOnlyList<ExchangeRate>> GetExchangeRatesAsync(Guid companyId,
        CancellationToken ct = default) =>
        await _db.ExchangeRates.Where(r => r.CompanyId == companyId)
            .OrderByDescending(r => r.RateDate).ThenBy(r => r.CurrencyCode)
            .Take(200).ToListAsync(ct);

    public async Task<IReadOnlyList<BankAccount>> GetBankAccountsAsync(Guid companyId,
        CancellationToken ct = default) =>
        await _db.BankAccounts.Where(b => b.CompanyId == companyId && b.IsActive)
            .OrderBy(b => b.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<TaxCode>> GetTaxCodesForSettingsAsync(Guid companyId,
        CancellationToken ct = default) =>
        await _db.TaxCodes.Where(t => t.CompanyId == companyId).OrderBy(t => t.Code).ToListAsync(ct);
}
