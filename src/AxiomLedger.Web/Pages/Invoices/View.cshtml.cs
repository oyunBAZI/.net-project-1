using AxiomLedger.Application;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Web.Pages.Invoices;

public sealed class ViewModel : AppPageModel
{
    private readonly SalesService _sales;
    private readonly CompanyService _companies;
    private readonly IAccountingDbContext _db;

    public ViewModel(SalesService sales, CompanyService companies, IAccountingDbContext db)
    {
        _sales = sales;
        _companies = companies;
        _db = db;
    }

    public SalesInvoice Invoice { get; private set; } = new();
    public Company Company { get; private set; } = new();
    public List<AllocationView> Allocations { get; } = new();
    public sealed record AllocationView(Guid PaymentId, string Number, DateOnly Date, decimal Amount,
        PaymentMethod Method);

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewJournal)) return ForbiddenResult();
        return await LoadAsync(id);
    }

    public async Task<IActionResult> OnPostIssueAsync(Guid id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _sales.IssueAsync(CompanyId.Value, id))) return await LoadAsync(id);
        Success("Invoice issued and posted.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostVoidAsync(Guid id, string reason)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!await TryAsync(() => _sales.VoidAsync(CompanyId.Value, id, reason))) return await LoadAsync(id);
        Success("Invoice voided.");
        return RedirectToPage(new { id });
    }

    private async Task<IActionResult> LoadAsync(Guid id)
    {
        var invoice = await _sales.GetAsync(CompanyId!.Value, id);
        if (invoice is null) return NotFound();
        Invoice = invoice;
        Company = await _companies.GetAsync(CompanyId.Value) ?? new Company();

        var allocations = await _db.PaymentAllocations.AsNoTracking()
            .Where(a => a.SalesInvoiceId == id && a.Payment != null)
            .Select(a => new { a.PaymentId, a.Payment!.Number, a.Payment.PaymentDate, a.Amount, a.Payment.Method })
            .ToListAsync();

        Allocations.AddRange(allocations.Select(a =>
            new AllocationView(a.PaymentId, a.Number, a.PaymentDate, a.Amount, a.Method)));

        return Page();
    }
}
