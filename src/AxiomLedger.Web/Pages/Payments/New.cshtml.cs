using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AxiomLedger.Web.Pages.Payments;

public sealed class NewModel : AppPageModel
{
    private readonly PaymentService _payments;
    private readonly ReferenceDataService _reference;
    private readonly SalesService _sales;
    private readonly PurchaseService _purchases;
    private readonly CompanyService _companies;

    public NewModel(PaymentService payments, ReferenceDataService reference, SalesService sales,
        PurchaseService purchases, CompanyService companies)
    {
        _payments = payments;
        _reference = reference;
        _sales = sales;
        _purchases = purchases;
        _companies = companies;
    }

    [BindProperty] public PaymentRequest Input { get; set; } = new();

    public PaymentDirection Direction { get; private set; } = PaymentDirection.Receipt;
    public Guid? SelectedParty { get; private set; }
    public IReadOnlyList<PartyOption> Parties { get; private set; } = Array.Empty<PartyOption>();
    public IReadOnlyList<BankAccount> BankAccounts { get; private set; } = Array.Empty<BankAccount>();
    public IReadOnlyList<AccountOption> CashAccounts { get; private set; } = Array.Empty<AccountOption>();
    public IReadOnlyList<Currency> Currencies { get; private set; } = Array.Empty<Currency>();
    public IReadOnlyList<OpenDocument> Documents { get; private set; } = Array.Empty<OpenDocument>();

    public async Task<IActionResult> OnGetAsync(Guid? partyId, PaymentDirection? direction, Guid? invoiceId,
        Guid? billId)
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ManagePayments)) return ForbiddenResult();

        Direction = direction ?? (billId.HasValue ? PaymentDirection.Disbursement : PaymentDirection.Receipt);
        SelectedParty = partyId;
        await LoadReferenceAsync();

        var company = await _companies.GetAsync(CompanyId.Value);
        Input = new PaymentRequest
        {
            Direction = Direction,
            PaymentDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CurrencyCode = company?.BaseCurrencyCode ?? "USD",
            Method = PaymentMethod.BankTransfer
        };

        if (invoiceId is { } salesInvoiceId)
        {
            var invoice = await _sales.GetAsync(CompanyId.Value, salesInvoiceId);
            if (invoice is not null)
            {
                Direction = PaymentDirection.Receipt;
                Input.Direction = Direction;
                SelectedParty = invoice.PartyId;
                Input.PartyId = invoice.PartyId;
                Input.Amount = invoice.BalanceDue;
                Input.CurrencyCode = invoice.CurrencyCode;
            }
        }
        else if (billId is { } vendorBillId)
        {
            var bill = await _purchases.GetAsync(CompanyId.Value, vendorBillId);
            if (bill is not null)
            {
                Direction = PaymentDirection.Disbursement;
                Input.Direction = Direction;
                SelectedParty = bill.PartyId;
                Input.PartyId = bill.PartyId;
                Input.Amount = bill.BalanceDue;
                Input.CurrencyCode = bill.CurrencyCode;
            }
        }

        if (SelectedParty is { } party)
            Documents = await _payments.GetOpenDocumentsAsync(CompanyId.Value, party, Direction);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ManagePayments)) return ForbiddenResult();

        await LoadReferenceAsync();
        Direction = Input.Direction;

        var destination = Request.Form["destination"].ToString();
        if (destination.StartsWith("bank:", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(destination[5..], out var bankId))
            Input.BankAccountId = bankId;
        else if (destination.StartsWith("account:", StringComparison.OrdinalIgnoreCase) &&
                 Guid.TryParse(destination[8..], out var accountId))
            Input.ContraAccountId = accountId;

        Input.Allocations = new List<PaymentAllocationInput>();
        foreach (var key in Request.Form.Keys.Where(k => k.StartsWith("apply_", StringComparison.OrdinalIgnoreCase)))
        {
            if (!Request.Form[key].Any(v => v == "true")) continue;
            if (!Guid.TryParse(key[6..], out var documentId)) continue;

            var amountRaw = Request.Form["amount_" + documentId].ToString();
            decimal? amount = decimal.TryParse(amountRaw, out var parsed) ? parsed : null;
            if (amount is <= 0) continue;

            Input.Allocations.Add(Input.Direction == PaymentDirection.Receipt
                ? new PaymentAllocationInput { SalesInvoiceId = documentId, Amount = amount }
                : new PaymentAllocationInput { BillId = documentId, Amount = amount });
        }

        SelectedParty = Input.PartyId;
        if (SelectedParty is { } party)
            Documents = await _payments.GetOpenDocumentsAsync(CompanyId.Value, party, Direction);

        try
        {
            var payment = await _payments.RecordAsync(CompanyId.Value, Input);
            Success($"Payment {payment.Number} posted.");
            return RedirectToPage("/Payments/View", new { id = payment.Id });
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    private async Task LoadReferenceAsync()
    {
        Parties = await _reference.GetPartiesAsync(CompanyId!.Value,
            Direction == PaymentDirection.Receipt ? PartyType.Customer : PartyType.Vendor);
        BankAccounts = await _reference.GetBankAccountsAsync(CompanyId.Value);
        var cash = await _reference.GetAccountsByTypeAsync(CompanyId.Value, AccountType.Asset);
        CashAccounts = cash.Where(a => a.Group == AccountGroup.CurrentAsset).ToList();
        Currencies = await _reference.GetCurrenciesAsync();
    }
}
