using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Infrastructure;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages;

public sealed class IndexModel : AppPageModel
{
    private readonly ReportingService _reports;
    private readonly CompanyService _companies;
    private readonly DemoDataService _demo;

    public IndexModel(ReportingService reports, CompanyService companies, DemoDataService demo)
    {
        _reports = reports;
        _companies = companies;
        _demo = demo;
    }

    public DashboardSnapshot? Snapshot { get; private set; }
    public DateOnly Today { get; private set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly? LockDate { get; private set; }
    public bool ShowStarter { get; private set; }
    public string? ServiceError { get; private set; }
    private decimal MaxMonthly { get; set; } = 1m;

    public int Height(decimal value)
    {
        if (value <= 0) return 1;
        var percent = (int)Math.Round(value / MaxMonthly * 100m);
        return Math.Clamp(percent, 1, 100);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (CompanyId is null)
        {
            var options = await CompaniesForUserAsync();
            return options.Count == 0
                ? RedirectToPage("/Companies/New")
                : RedirectToPage("/SwitchCompany");
        }

        try
        {
            var company = await _companies.GetAsync(CompanyId.Value);
            if (company is null) return RedirectToPage("/SwitchCompany");

            Snapshot = await _reports.DashboardAsync(CompanyId.Value);
            Today = Snapshot.AsOf;
            LockDate = company.Settings?.LockDate;
            MaxMonthly = Math.Max(1m, Snapshot.MonthlySeries
                .SelectMany(p => new[] { p.Revenue, p.Expenses })
                .DefaultIfEmpty(0m).Max());
            ShowStarter = Snapshot.RecentActivity.Count == 0 && Snapshot.ReceivableBalance == 0m &&
                          Snapshot.PayableBalance == 0m;
        }
        catch (DomainException ex)
        {
            ServiceError = ex.Message;
        }
        catch (Exception ex)
        {
            ServiceError = "The dashboard could not be loaded: " + ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSeedDemoAsync()
    {
        if (!Can(Permission.ManageCompanies)) return ForbiddenResult();
        if (string.IsNullOrWhiteSpace(CurrentUserId)) return RedirectToPage("/Login");

        try
        {
            var companyId = await _demo.CreateDemoCompanyAsync(CurrentUserId);
            await SignInHelper.RefreshCompanyAsync(HttpContext, companyId, UserRole.Owner);
            Success("Sample company created. Explore the invoices, bills, bank feed and reports.");
            return RedirectToPage("/Index");
        }
        catch (DomainException ex)
        {
            Error(ex.Message);
            return RedirectToPage("/Index");
        }
    }

    private async Task<IReadOnlyList<CompanyOption>> CompaniesForUserAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentUserId)) return Array.Empty<CompanyOption>();
        return await _companies.ListForUserAsync(CurrentUserId);
    }
}
