using AxiomLedger.Application.Models;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

public sealed class AccountRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AccountType Type { get; set; } = AccountType.Expense;
    public AccountGroup Group { get; set; } = AccountGroup.OperatingExpense;
    public Guid? ParentId { get; set; }
    public string? Description { get; set; }
    public bool IsPostable { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public bool IsCashAccount { get; set; }
    public bool IsContra { get; set; }
    public CashFlowCategory CashFlowCategory { get; set; } = CashFlowCategory.Operating;
    public string? CurrencyCode { get; set; }
    public Guid? DefaultTaxCodeId { get; set; }
}

public sealed class TaxCodeRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public TaxKind Kind { get; set; } = TaxKind.Both;
    public Guid TaxAccountId { get; set; }
    public Guid? InputTaxAccountId { get; set; }
    public bool IsInclusive { get; set; }
    public bool IsActive { get; set; } = true;
    public string? ReportGroup { get; set; }
}

public sealed class ExchangeRateRequest
{
    public string CurrencyCode { get; set; } = string.Empty;
    public DateOnly RateDate { get; set; }
    public decimal Rate { get; set; }
    public string? Source { get; set; }
}

public sealed class DepartmentRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class SettingsService
{
    private readonly IAccountingDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public SettingsService(IAccountingDbContext db, ICurrentUser user, IClock clock, AuditService audit)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _audit = audit;
    }

    // ----- Chart of accounts -----

    public async Task<IReadOnlyList<Account>> ListAccountsAsync(Guid companyId, bool includeInactive = true,
        CancellationToken ct = default)
    {
        var query = _db.Accounts.Where(a => a.CompanyId == companyId);
        if (!includeInactive) query = query.Where(a => a.IsActive);
        return await query.OrderBy(a => a.Code).ToListAsync(ct);
    }

    public async Task<Account> CreateAccountAsync(Guid companyId, AccountRequest request,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageAccounts);
        await ValidateAccountAsync(companyId, request, null, ct);

        var account = new Account { CompanyId = companyId, CreatedAt = _clock.UtcNow };
        Apply(account, request);
        _db.Accounts.Add(account);
        _audit.Record(companyId, "AccountCreated", nameof(Account), account.Id.ToString(),
            $"{account.Code} {account.Name}");
        await _db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<Account> UpdateAccountAsync(Guid companyId, Guid id, AccountRequest request,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageAccounts);
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Account not found.");

        await ValidateAccountAsync(companyId, request, id, ct);

        if (account.Type != request.Type)
        {
            var hasActivity = await _db.JournalLines.AnyAsync(l => l.AccountId == id, ct);
            if (hasActivity)
                throw new DomainValidationException(
                    "The account type cannot change once journal lines have been posted to it.");
        }

        Apply(account, request);
        _audit.Record(companyId, "AccountUpdated", nameof(Account), account.Id.ToString(),
            $"{account.Code} {account.Name}");
        await _db.SaveChangesAsync(ct);
        return account;
    }

    public async Task SetAccountActiveAsync(Guid companyId, Guid id, bool active, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageAccounts);
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Account not found.");
        if (!active && account.IsSystem)
            throw new DomainValidationException("System control accounts cannot be deactivated.");

        account.IsActive = active;
        _audit.Record(companyId, active ? "AccountActivated" : "AccountDeactivated",
            nameof(Account), account.Id.ToString());
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAccountAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageAccounts);
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == companyId, ct)
            ?? throw new NotFoundException("Account not found.");
        if (account.IsSystem)
            throw new DomainValidationException("System accounts cannot be deleted.");
        if (await _db.JournalLines.AnyAsync(l => l.AccountId == id, ct))
            throw new DomainValidationException("This account has journal activity. Deactivate it instead.");
        if (await _db.Accounts.AnyAsync(a => a.ParentId == id, ct))
            throw new DomainValidationException("Move or delete the sub-accounts first.");

        _db.Accounts.Remove(account);
        _audit.Record(companyId, "AccountDeleted", nameof(Account), account.Id.ToString(), account.Code);
        await _db.SaveChangesAsync(ct);
    }

    private async Task ValidateAccountAsync(Guid companyId, AccountRequest request, Guid? id, CancellationToken ct)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Code)) errors.Add("An account code is required.");
        if (string.IsNullOrWhiteSpace(request.Name)) errors.Add("An account name is required.");

        var code = request.Code.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(request.Code) &&
            await _db.Accounts.AnyAsync(a => a.CompanyId == companyId && a.Code == code && a.Id != id, ct))
            errors.Add($"Account code {code} is already in use.");

        if (!GroupMatchesType(request.Type, request.Group))
            errors.Add("The selected statement group does not belong to that account type.");

        if (request.ParentId is { } parentId)
        {
            if (parentId == id) errors.Add("An account cannot be its own parent.");
            var parent = await _db.Accounts.FirstOrDefaultAsync(a =>
                a.Id == parentId && a.CompanyId == companyId, ct);
            if (parent is null) errors.Add("The parent account does not exist.");
            else if (parent.Type != request.Type) errors.Add("The parent account has a different type.");
        }

        if (request.IsCashAccount && request.Type != AccountType.Asset)
            errors.Add("Only asset accounts can be marked as cash accounts.");

        if (errors.Count > 0) throw new DomainValidationException(errors);
    }

    private static bool GroupMatchesType(AccountType type, AccountGroup group) => type switch
    {
        AccountType.Asset => group is AccountGroup.CurrentAsset or AccountGroup.FixedAsset or AccountGroup.OtherAsset,
        AccountType.Liability => group is AccountGroup.CurrentLiability or AccountGroup.NonCurrentLiability,
        AccountType.Equity => group == AccountGroup.Equity,
        AccountType.Revenue => group is AccountGroup.Revenue or AccountGroup.OtherIncome,
        AccountType.Expense => group is AccountGroup.CostOfSales or AccountGroup.OperatingExpense
            or AccountGroup.OtherExpense,
        _ => false
    };

    private static void Apply(Account account, AccountRequest request)
    {
        account.Code = request.Code.Trim().ToUpperInvariant();
        account.Name = request.Name.Trim();
        account.Type = request.Type;
        account.Group = request.Group;
        account.ParentId = request.ParentId;
        account.Description = request.Description;
        account.IsPostable = request.IsPostable;
        account.IsActive = request.IsActive;
        account.IsCashAccount = request.IsCashAccount;
        account.IsContra = request.IsContra;
        account.CashFlowCategory = request.CashFlowCategory;
        account.CurrencyCode = request.CurrencyCode;
        account.DefaultTaxCodeId = request.DefaultTaxCodeId;
    }

    // ----- Tax codes -----

    public async Task<IReadOnlyList<TaxCode>> ListTaxCodesAsync(Guid companyId, CancellationToken ct = default) =>
        await _db.TaxCodes.Where(t => t.CompanyId == companyId).OrderBy(t => t.Code).ToListAsync(ct);

    public async Task<TaxCode> SaveTaxCodeAsync(Guid companyId, TaxCodeRequest request, Guid? id = null,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageSettings);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Code)) errors.Add("A tax code is required.");
        if (string.IsNullOrWhiteSpace(request.Name)) errors.Add("A tax name is required.");
        if (request.Rate is < 0 or > 100) errors.Add("The tax rate must be between 0 and 100.");
        if (request.TaxAccountId == Guid.Empty) errors.Add("Select the tax liability account.");
        if (errors.Count > 0) throw new DomainValidationException(errors);

        var code = request.Code.Trim().ToUpperInvariant();
        if (await _db.TaxCodes.AnyAsync(t => t.CompanyId == companyId && t.Code == code && t.Id != id, ct))
            throw new DomainValidationException($"Tax code {code} already exists.");

        TaxCode tax;
        if (id is { } taxId)
        {
            tax = await _db.TaxCodes.FirstOrDefaultAsync(t => t.Id == taxId && t.CompanyId == companyId, ct)
                ?? throw new NotFoundException("Tax code not found.");
        }
        else
        {
            tax = new TaxCode { CompanyId = companyId };
            _db.TaxCodes.Add(tax);
        }

        tax.Code = code;
        tax.Name = request.Name.Trim();
        tax.Rate = request.Rate;
        tax.Kind = request.Kind;
        tax.TaxAccountId = request.TaxAccountId;
        tax.InputTaxAccountId = request.InputTaxAccountId;
        tax.IsInclusive = request.IsInclusive;
        tax.IsActive = request.IsActive;
        tax.ReportGroup = request.ReportGroup;

        _audit.Record(companyId, id is null ? "TaxCodeCreated" : "TaxCodeUpdated", nameof(TaxCode),
            tax.Id.ToString(), tax.Code);
        await _db.SaveChangesAsync(ct);
        return tax;
    }

    // ----- Exchange rates -----

    public async Task<ExchangeRate> UpsertRateAsync(Guid companyId, ExchangeRateRequest request,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageSettings);
        var currency = request.CurrencyCode.Trim().ToUpperInvariant();
        if (currency.Length != 3) throw new DomainValidationException("Use a three-letter ISO currency code.");
        if (request.Rate <= 0) throw new DomainValidationException("The rate must be greater than zero.");

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId, ct);
        if (currency == company.BaseCurrencyCode)
            throw new DomainValidationException("The base currency always has a rate of 1.");

        if (!await _db.Currencies.AnyAsync(c => c.Code == currency, ct))
            _db.Currencies.Add(new Currency { Code = currency, Name = currency, Symbol = currency });

        var rate = await _db.ExchangeRates.FirstOrDefaultAsync(r =>
            r.CompanyId == companyId && r.CurrencyCode == currency && r.RateDate == request.RateDate, ct);
        if (rate is null)
        {
            rate = new ExchangeRate
            {
                CompanyId = companyId,
                CurrencyCode = currency,
                RateDate = request.RateDate,
                CreatedAt = _clock.UtcNow
            };
            _db.ExchangeRates.Add(rate);
        }

        rate.Rate = request.Rate;
        rate.Source = request.Source;
        _audit.Record(companyId, "ExchangeRateSet", nameof(ExchangeRate), rate.Id.ToString(),
            $"{currency} {request.RateDate:yyyy-MM-dd} = {request.Rate}");
        await _db.SaveChangesAsync(ct);
        return rate;
    }

    // ----- Departments -----

    public async Task<Department> SaveDepartmentAsync(Guid companyId, DepartmentRequest request, Guid? id = null,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageSettings);
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
            throw new DomainValidationException("A department code and name are required.");

        var code = request.Code.Trim().ToUpperInvariant();
        if (await _db.Departments.AnyAsync(d => d.CompanyId == companyId && d.Code == code && d.Id != id, ct))
            throw new DomainValidationException($"Department {code} already exists.");

        Department department;
        if (id is { } departmentId)
        {
            department = await _db.Departments.FirstOrDefaultAsync(d =>
                d.Id == departmentId && d.CompanyId == companyId, ct)
                ?? throw new NotFoundException("Department not found.");
        }
        else
        {
            department = new Department { CompanyId = companyId };
            _db.Departments.Add(department);
        }

        department.Code = code;
        department.Name = request.Name.Trim();
        department.IsActive = request.IsActive;
        _audit.Record(companyId, id is null ? "DepartmentCreated" : "DepartmentUpdated",
            nameof(Department), department.Id.ToString(), department.Code);
        await _db.SaveChangesAsync(ct);
        return department;
    }

    public async Task<IReadOnlyList<Department>> ListDepartmentsAsync(Guid companyId,
        CancellationToken ct = default) =>
        await _db.Departments.Where(d => d.CompanyId == companyId).OrderBy(d => d.Code).ToListAsync(ct);
}
