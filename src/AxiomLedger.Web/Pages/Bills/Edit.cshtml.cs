using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Bills;

public sealed class EditModel : AppPageModel
{
    private readonly PurchaseService _purchases;
    private readonly ReferenceDataService _reference;
    private readonly CompanyService _companies;

    public EditModel(PurchaseService purchases, ReferenceDataService reference, CompanyService companies)
    {
        _purchases = purchases;
        _reference = reference;
        _companies = companies;
    }

    public Guid? Id { get; private set; }
    public bool IsNew => Id is null;

    [BindProperty] public BillEditModel Input { get; set; } = new();

    public IReadOnlyList<PartyOption> Vendors { get; private set; } = Array.Empty<PartyOption>();
    public IReadOnlyList<AccountOption> ExpenseAccounts { get; private set; } = Array.Empty<AccountOption>();
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
        if (!Can(Permission.ManagePurchases)) return ForbiddenResult();

        await LoadReferenceAsync(CompanyId.Value);
        var settings = await _companies.GetSettingsAsync(CompanyId.Value);
        Input.IssueDate = DateOnly.FromDateTime(DateTime.UtcNow);
        Input.DueDate = Input.IssueDate.AddDays(settings.DefaultInvoiceDueDays <= 0 ? 30 : settings.DefaultInvoiceDueDays);
        Input.CurrencyCode = Currencies.FirstOrDefault()?.Code ?? "USD";

        if (id is { } billId)
        {
            var bill = await _purchases.GetAsync(CompanyId.Value, billId);
            if (bill is null) return NotFound();
            if (!bill.IsEditable)
            {
                Error("Only draft bills can be edited.");
                return RedirectToPage("/Bills/View", new { id = billId });
            }

            Id = bill.Id;
            Input = new BillEditModel
            {
                PartyId = bill.PartyId,
                VendorInvoiceNumber = bill.VendorInvoiceNumber,
                IssueDate = bill.IssueDate,
                DueDate = bill.DueDate,
                CurrencyCode = bill.CurrencyCode,
                ExchangeRate = bill.ExchangeRate,
                Notes = bill.Notes,
                Lines = bill.Lines.OrderBy(l => l.LineNumber).Select(l => new InvoiceLineInput
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
        if (!Can(Permission.ManagePurchases)) return ForbiddenResult();

        Id = id;
        await LoadReferenceAsync(CompanyId.Value);

        if (string.IsNullOrWhiteSpace(Input.CurrencyCode))
            Input.CurrencyCode = Currencies.FirstOrDefault()?.Code ?? "USD";

        Input.Lines = Input.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Description) || l.UnitPrice != 0)
            .ToList();
        if (Input.Lines.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Add at least one bill line.");
            return Page();
        }

        try
        {
            var bill = id is null
                ? await _purchases.CreateDraftAsync(CompanyId.Value, Input)
                : await _purchases.UpdateDraftAsync(CompanyId.Value, id.Value, Input);

            if (string.Equals(action, "receive", StringComparison.OrdinalIgnoreCase))
            {
                await _purchases.ReceiveAsync(CompanyId.Value, bill.Id);
                Success($"Bill {bill.Number} received and posted.");
                return RedirectToPage("/Bills/View", new { id = bill.Id });
            }

            Success("Draft saved.");
            return RedirectToPage("/Bills/View", new { id = bill.Id });
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    private async Task LoadReferenceAsync(Guid companyId)
    {
        Vendors = await _reference.GetPartiesAsync(companyId, PartyType.Vendor);
        var expenses = await _reference.GetAccountsByTypeAsync(companyId, AccountType.Expense);
        var assets = await _reference.GetAccountsByTypeAsync(companyId, AccountType.Asset);
        ExpenseAccounts = expenses.Concat(assets).OrderBy(a => a.Code).ToList();
        TaxCodes = await _reference.GetTaxCodesAsync(companyId, TaxKind.Purchase);
        Currencies = await _reference.GetCurrenciesAsync();
    }
}
