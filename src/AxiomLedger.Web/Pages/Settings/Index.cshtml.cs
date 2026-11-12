using AxiomLedger.Application;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Web.Pages.Settings;

public sealed class IndexModel : AppPageModel
{
    private readonly CompanyService _companies;
    private readonly SettingsService _settings;
    private readonly ReferenceDataService _reference;
    private readonly IAccountingDbContext _db;

    public IndexModel(CompanyService companies, SettingsService settings, ReferenceDataService reference,
        IAccountingDbContext db)
    {
        _companies = companies;
        _settings = settings;
        _reference = reference;
        _db = db;
    }

    [BindProperty] public CompanyEditRequest Company { get; set; } = new();
    [BindProperty] public CompanySettingsRequest Preferences { get; set; } = new();

    public string BaseCurrency { get; private set; } = "USD";
    public string FiscalStartLabel { get; private set; } = "January";
    public DateOnly? LockDate { get; private set; }
    public int MemberCount { get; private set; }
    public int AccountCount { get; private set; }
    public int JournalCount { get; private set; }
    public IReadOnlyList<TaxCode> TaxCodes { get; private set; } = Array.Empty<TaxCode>();

    public async Task<IActionResult> OnGetAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ManageSettings)) return ForbiddenResult();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCompanyAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _companies.UpdateAsync(CompanyId.Value, Company)))
        {
            await LoadAsync();
            return Page();
        }
        Success("Company details saved.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPreferencesAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _companies.UpdateSettingsAsync(CompanyId.Value, Preferences)))
        {
            await LoadAsync();
            return Page();
        }
        Success("Accounting preferences saved.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostLockAsync(DateOnly? lockDate, bool unlock = false)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        var target = unlock ? null : lockDate;
        if (!await TryAsync(() => _companies.SetLockDateAsync(CompanyId.Value, target)))
            return RedirectToPage();

        Success(target is null ? "Books unlocked." : $"Books locked through {target:dd MMM yyyy}.");
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        var company = await _companies.GetAsync(CompanyId!.Value);
        var settings = await _companies.GetSettingsAsync(CompanyId.Value);
        TaxCodes = await _reference.GetTaxCodesForSettingsAsync(CompanyId.Value);

        BaseCurrency = company?.BaseCurrencyCode ?? "USD";
        FiscalStartLabel = new DateTime(2000, company?.FiscalYearStartMonth ?? 1, 1).ToString("MMMM");
        LockDate = settings.LockDate;
        MemberCount = await _db.CompanyMemberships.CountAsync(m => m.CompanyId == CompanyId && m.IsActive);
        AccountCount = await _db.Accounts.CountAsync(a => a.CompanyId == CompanyId);
        JournalCount = await _db.JournalEntries.CountAsync(j => j.CompanyId == CompanyId);

        Company = new CompanyEditRequest
        {
            Name = company?.Name ?? string.Empty,
            LegalName = company?.LegalName,
            TaxId = company?.TaxId,
            RegistrationNumber = company?.RegistrationNumber,
            CountryCode = company?.CountryCode ?? "US",
            AddressLine1 = company?.AddressLine1,
            AddressLine2 = company?.AddressLine2,
            City = company?.City,
            Region = company?.Region,
            PostalCode = company?.PostalCode,
            Email = company?.Email,
            Phone = company?.Phone
        };

        Preferences = new CompanySettingsRequest
        {
            RequireJournalApproval = settings.RequireJournalApproval,
            ApprovalThreshold = settings.ApprovalThreshold,
            RequireSegregationOfDuties = settings.RequireSegregationOfDuties,
            DefaultInvoiceDueDays = settings.DefaultInvoiceDueDays,
            LockDate = settings.LockDate,
            DefaultSalesTaxCodeId = settings.DefaultSalesTaxCodeId,
            DefaultPurchaseTaxCodeId = settings.DefaultPurchaseTaxCodeId,
            InvoicePrefix = settings.InvoicePrefix,
            BillPrefix = settings.BillPrefix,
            JournalPrefix = settings.JournalPrefix,
            PaymentPrefix = settings.PaymentPrefix,
            LateFeePercent = settings.LateFeePercent
        };
    }
}
