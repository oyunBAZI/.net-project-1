using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Invoices;

public sealed class EditModel : AppPageModel
{
    private readonly SalesService _sales;
    private readonly ReferenceDataService _reference;
    private readonly CompanyService _companies;

    public EditModel(SalesService sales, ReferenceDataService reference, CompanyService companies)
    {
        _sales = sales;
        _reference = reference;
        _companies = companies;
    }

    public Guid? Id { get; private set; }
    public bool IsNew => Id is null;

    [BindProperty] public InvoiceEditModel Input { get; set; } = new();

    public IReadOnlyList<PartyOption> Customers { get; private set; } = Array.Empty<PartyOption>();
    public IReadOnlyList<AccountOption> RevenueAccounts { get; private set; } = Array.Empty<AccountOption>();
    public IReadOnlyList<TaxCode> TaxCodes { get; private set; } = Array.Empty<TaxCode>();
    public IReadOnlyList<Currency> Currencies { get; private set; } = Array.Empty<Currency>();

    public decimal LineTotal(InvoiceLineInput line)
    {
        var gross = line.Quantity * line.UnitPrice;
        var net = gross - (gross * line.DiscountPercent / 100m);
        return Money.Round(net * (1 + line.TaxRate / 100m));
    }

    public async Task<IActionResult> OnGetAsync(Guid? id)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ManageSales)) return ForbiddenResult();

        await LoadReferenceAsync(CompanyId.Value);
        var settings = await _companies.GetSettingsAsync(CompanyId.Value);
        Input.IssueDate = DateOnly.FromDateTime(DateTime.UtcNow);
        Input.DueDate = Input.IssueDate.AddDays(settings.DefaultInvoiceDueDays <= 0 ? 30 : settings.DefaultInvoiceDueDays);
        Input.CurrencyCode = Currencies.FirstOrDefault()?.Code ?? "USD";

        if (id is { } invoiceId)
        {
            var invoice = await _sales.GetAsync(CompanyId.Value, invoiceId);
            if (invoice is null) return NotFound();
            if (!invoice.IsEditable)
            {
                Error("Only draft invoices can be edited.");
                return RedirectToPage("/Invoices/View", new { id = invoiceId });
            }

            Id = invoice.Id;
            Input = new InvoiceEditModel
            {
                PartyId = invoice.PartyId,
                Kind = invoice.Kind,
                IssueDate = invoice.IssueDate,
                DueDate = invoice.DueDate,
                CurrencyCode = invoice.CurrencyCode,
                ExchangeRate = invoice.ExchangeRate,
                PurchaseOrderNumber = invoice.PurchaseOrderNumber,
                Notes = invoice.Notes,
                Lines = invoice.Lines.OrderBy(l => l.LineNumber).Select(l => new InvoiceLineInput
                {
                    Description = l.Description,
                    AccountId = l.AccountId,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice,
                    DiscountPercent = l.DiscountPercent,
                    TaxCodeId = l.TaxCodeId,
                    TaxRate = l.TaxRate
                }).ToList()
            };
        }

        if (Input.Lines.Count == 0)
            Input.Lines.Add(new InvoiceLineInput { Quantity = 1m });

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid? id, string action = "save")
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ManageSales)) return ForbiddenResult();

        Id = id;
        await LoadReferenceAsync(CompanyId.Value);

        if (string.IsNullOrWhiteSpace(Input.CurrencyCode))
            Input.CurrencyCode = Currencies.FirstOrDefault()?.Code ?? "USD";

        Input.Lines = Input.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Description) || l.UnitPrice != 0)
            .ToList();
        if (Input.Lines.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Add at least one invoice line.");
            return Page();
        }

        try
        {
            var invoice = id is null
                ? await _sales.CreateDraftAsync(CompanyId.Value, Input)
                : await _sales.UpdateDraftAsync(CompanyId.Value, id.Value, Input);

            if (string.Equals(action, "issue", StringComparison.OrdinalIgnoreCase))
            {
                await _sales.IssueAsync(CompanyId.Value, invoice.Id);
                Success($"Invoice {invoice.Number} issued and posted to the ledger.");
                return RedirectToPage("/Invoices/View", new { id = invoice.Id });
            }

            Success("Draft saved.");
            return RedirectToPage("/Invoices/View", new { id = invoice.Id });
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    private async Task LoadReferenceAsync(Guid companyId)
    {
        Customers = await _reference.GetPartiesAsync(companyId, PartyType.Customer);
        RevenueAccounts = await _reference.GetAccountsByTypeAsync(companyId, AccountType.Revenue);
        TaxCodes = await _reference.GetTaxCodesAsync(companyId, TaxKind.Sales);
        Currencies = await _reference.GetCurrenciesAsync();
    }
}
