using AxiomLedger.Application.Models;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

public sealed class CompanyService
{
    private readonly IAccountingDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly FiscalCalendarService _calendar;

    public CompanyService(IAccountingDbContext db, ICurrentUser user, IClock clock, AuditService audit,
        FiscalCalendarService calendar)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _audit = audit;
        _calendar = calendar;
    }

    public async Task<bool> IsFirstRunAsync(CancellationToken ct = default) =>
        !await _db.Companies.AnyAsync(ct);

    public async Task<IReadOnlyList<CompanyOption>> ListForUserAsync(string userId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(userId, out var userGuid)) return Array.Empty<CompanyOption>();

        var memberships = await _db.CompanyMemberships
            .Where(m => m.UserId == userGuid && m.IsActive)
            .Join(_db.Companies, m => m.CompanyId, c => c.Id, (m, c) => new { m, c })
            .OrderBy(x => x.c.Name)
            .ToListAsync(ct);

        return memberships.Select(x => new CompanyOption(x.c.Id, x.c.Name, x.m.Role, x.c.BaseCurrencyCode)).ToList();
    }

    public async Task<Company?> GetAsync(Guid companyId, CancellationToken ct = default) =>
        await _db.Companies.Include(c => c.Settings).FirstOrDefaultAsync(c => c.Id == companyId, ct);

    public async Task<Company> CreateAsync(NewCompanyRequest request, string ownerUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new DomainValidationException("A company name is required.");
        if (string.IsNullOrWhiteSpace(request.BaseCurrencyCode) || request.BaseCurrencyCode.Trim().Length != 3)
            throw new DomainValidationException("The base currency must be a three-letter ISO code.");
        if (request.FiscalYearStartMonth is < 1 or > 12)
            throw new DomainValidationException("The fiscal year start month must be between 1 and 12.");

        if (!Guid.TryParse(ownerUserId, out var ownerGuid))
            throw new NotFoundException("The owner account was not found.");
        var owner = await _db.Users.FirstOrDefaultAsync(u => u.Id == ownerGuid, ct)
            ?? throw new NotFoundException("The owner account was not found.");

        var currency = request.BaseCurrencyCode.Trim().ToUpperInvariant();
        if (!await _db.Currencies.AnyAsync(c => c.Code == currency, ct))
            _db.Currencies.Add(new Currency { Code = currency, Name = currency, Symbol = currency, DecimalPlaces = 2 });

        var company = new Company
        {
            Name = request.Name.Trim(),
            LegalName = request.LegalName,
            TaxId = request.TaxId,
            RegistrationNumber = request.RegistrationNumber,
            BaseCurrencyCode = currency,
            CountryCode = request.CountryCode,
            FiscalYearStartMonth = request.FiscalYearStartMonth,
            AddressLine1 = request.AddressLine1,
            City = request.City,
            Region = request.Region,
            PostalCode = request.PostalCode,
            Email = request.Email,
            Phone = request.Phone,
            CreatedAt = _clock.UtcNow
        };
        _db.Companies.Add(company);

        company.Settings = new CompanySettings
        {
            CompanyId = company.Id,
            DefaultInvoiceDueDays = 30,
            RequireSegregationOfDuties = true,
            InvoicePrefix = "INV",
            BillPrefix = "BILL",
            JournalPrefix = "JE",
            PaymentPrefix = "PMT"
        };
        _db.CompanySettings.Add(company.Settings);

        var accounts = new List<Account>();
        foreach (var (template, index) in ChartOfAccountsTemplate.For(request.Industry).Select((a, i) => (a, i)))
        {
            var account = new Account
            {
                CompanyId = company.Id,
                Code = template.Code,
                Name = template.Name,
                Type = template.Type,
                Group = template.Group,
                Description = template.Description,
                IsCashAccount = template.IsCash,
                IsContra = template.IsContra,
                IsPostable = true,
                IsSystem = template.Code is "1100" or "2000" or "2100" or "1400" or "3100",
                CashFlowCategory = AccountGroups.CashFlowDefault(template.Group),
                CreatedAt = _clock.UtcNow,
                SortOrder = index
            };
            accounts.Add(account);
            _db.Accounts.Add(account);
        }

        var ar = accounts.First(a => a.Code == "1100");
        var ap = accounts.First(a => a.Code == "2000");
        var salesTax = accounts.First(a => a.Code == "2100");
        var inputTax = accounts.First(a => a.Code == "1400");
        var retained = accounts.First(a => a.Code == "3100");

        var standard = new TaxCode
        {
            CompanyId = company.Id,
            Code = "TAX20",
            Name = "Standard rate 20%",
            Rate = 20m,
            Kind = TaxKind.Both,
            TaxAccountId = salesTax.Id,
            InputTaxAccountId = inputTax.Id,
            ReportGroup = "Standard"
        };
        var reduced = new TaxCode
        {
            CompanyId = company.Id,
            Code = "TAX10",
            Name = "Reduced rate 10%",
            Rate = 10m,
            Kind = TaxKind.Both,
            TaxAccountId = salesTax.Id,
            InputTaxAccountId = inputTax.Id,
            ReportGroup = "Reduced"
        };
        var zero = new TaxCode
        {
            CompanyId = company.Id,
            Code = "ZERO",
            Name = "Zero rated / exempt",
            Rate = 0m,
            Kind = TaxKind.Both,
            TaxAccountId = salesTax.Id,
            InputTaxAccountId = inputTax.Id,
            ReportGroup = "Zero"
        };
        _db.TaxCodes.AddRange(standard, reduced, zero);

        company.Settings.DefaultSalesTaxCodeId = zero.Id;
        company.Settings.DefaultPurchaseTaxCodeId = zero.Id;
        company.Settings.RetainedEarningsAccountId = retained.Id;
        company.Settings.AccountsReceivableAccountId = ar.Id;
        company.Settings.AccountsPayableAccountId = ap.Id;

        _db.CompanyMemberships.Add(new CompanyMembership
        {
            UserId = owner.Id,
            CompanyId = company.Id,
            Role = UserRole.Owner,
            IsActive = true,
            CreatedAt = _clock.UtcNow
        });

        await _calendar.EnsureYearAsync(company.Id, _clock.Today, ct);

        _audit.Record(company.Id, "CompanyCreated", nameof(Company), company.Id.ToString(), company.Name);
        await _db.SaveChangesAsync(ct);

        return company;
    }

    public async Task UpdateAsync(Guid companyId, CompanyEditRequest request, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageSettings);
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new DomainValidationException("A company name is required.");

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
            ?? throw new NotFoundException("Company not found.");

        company.Name = request.Name.Trim();
        company.LegalName = request.LegalName;
        company.TaxId = request.TaxId;
        company.RegistrationNumber = request.RegistrationNumber;
        company.CountryCode = request.CountryCode;
        company.AddressLine1 = request.AddressLine1;
        company.AddressLine2 = request.AddressLine2;
        company.City = request.City;
        company.Region = request.Region;
        company.PostalCode = request.PostalCode;
        company.Email = request.Email;
        company.Phone = request.Phone;

        _audit.Record(companyId, "CompanyUpdated", nameof(Company), company.Id.ToString());
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateSettingsAsync(Guid companyId, CompanySettingsRequest request,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageSettings);
        if (request.ApprovalThreshold < 0)
            throw new DomainValidationException("The approval threshold cannot be negative.");
        if (request.LockDate.HasValue && request.LockDate.Value > _clock.Today)
            throw new DomainValidationException("The lock date cannot be in the future.");
        if (request.DefaultInvoiceDueDays is < 0 or > 365)
            throw new DomainValidationException("Default payment terms must be between 0 and 365 days.");

        var settings = await GetOrCreateSettingsAsync(companyId, ct);
        settings.RequireJournalApproval = request.RequireJournalApproval;
        settings.ApprovalThreshold = Money.Round(request.ApprovalThreshold);
        settings.RequireSegregationOfDuties = request.RequireSegregationOfDuties;
        settings.DefaultInvoiceDueDays = request.DefaultInvoiceDueDays;
        settings.LockDate = request.LockDate;
        settings.DefaultSalesTaxCodeId = request.DefaultSalesTaxCodeId;
        settings.DefaultPurchaseTaxCodeId = request.DefaultPurchaseTaxCodeId;
        settings.InvoicePrefix = Prefix(request.InvoicePrefix, "INV");
        settings.BillPrefix = Prefix(request.BillPrefix, "BILL");
        settings.JournalPrefix = Prefix(request.JournalPrefix, "JE");
        settings.PaymentPrefix = Prefix(request.PaymentPrefix, "PMT");
        settings.LateFeePercent = request.LateFeePercent;

        _audit.Record(companyId, "SettingsUpdated", nameof(CompanySettings), settings.Id.ToString());
        await _db.SaveChangesAsync(ct);
    }

    private static string Prefix(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToUpperInvariant();

    private async Task<CompanySettings> GetOrCreateSettingsAsync(Guid companyId, CancellationToken ct)
    {
        var settings = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        if (settings is null)
        {
            settings = new CompanySettings { CompanyId = companyId };
            _db.CompanySettings.Add(settings);
        }
        return settings;
    }

    public async Task<CompanySettings> GetSettingsAsync(Guid companyId, CancellationToken ct = default) =>
        await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId, ct)
        ?? new CompanySettings { CompanyId = companyId };

    public async Task SetLockDateAsync(Guid companyId, DateOnly? lockDate, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ClosePeriods);
        if (lockDate.HasValue && lockDate.Value > _clock.Today)
            throw new DomainValidationException("The lock date cannot be in the future.");

        var settings = await GetOrCreateSettingsAsync(companyId, ct);
        settings.LockDate = lockDate;
        _audit.Record(companyId, lockDate is null ? "BooksUnlocked" : "BooksLocked",
            nameof(CompanySettings), settings.Id.ToString(), lockDate?.ToString("yyyy-MM-dd"));
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<MembershipView>> ListMembersAsync(Guid companyId,
        CancellationToken ct = default)
    {
        var rows = await _db.CompanyMemberships
            .Where(m => m.CompanyId == companyId)
            .Join(_db.Users, m => m.UserId, u => u.Id, (m, u) => new
            {
                MembershipId = m.Id,
                UserId = u.Id,
                u.Email,
                u.DisplayName,
                m.Role,
                m.IsActive,
                m.CreatedAt,
                u.LastLoginAt
            })
            .ToListAsync(ct);

        return rows
            .OrderBy(r => r.DisplayName)
            .Select(r => new MembershipView(r.MembershipId, r.UserId, r.Email, r.DisplayName, r.Role,
                r.IsActive, r.CreatedAt, r.LastLoginAt))
            .ToList();
    }

    public async Task<MembershipView?> GetMembershipAsync(Guid companyId, string userId,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(userId, out var id)) return null;
        return await _db.CompanyMemberships
            .Where(m => m.CompanyId == companyId && m.UserId == id && m.IsActive)
            .Join(_db.Users, m => m.UserId, u => u.Id, (m, u) => new MembershipView(
                m.Id, u.Id, u.Email, u.DisplayName, m.Role, m.IsActive, m.CreatedAt, u.LastLoginAt))
            .FirstOrDefaultAsync(ct);
    }
}
