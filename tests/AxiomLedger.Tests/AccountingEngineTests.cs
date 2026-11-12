using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;

namespace AxiomLedger.Tests;

public class JournalPostingTests
{
    [Fact]
    public async Task Balanced_journal_posts_and_appears_in_the_trial_balance()
    {
        await using var company = await TestCompany.CreateAsync();

        await company.PostJournalAsync("Owner investment",
            ("1010", 25_000m, 0m),
            ("3000", 0m, 25_000m));

        var report = await company.Service<ReportingService>()
            .TrialBalanceAsync(company.CompanyId, company.Today, company.Today);

        Assert.True(report.IsBalanced);
        Assert.Equal(25_000m, report.TotalDebit);
        var cash = Assert.Single(report.Rows, r => r.Code == "1010");
        Assert.Equal(25_000m, cash.ClosingBalance);
    }

    [Fact]
    public async Task Unbalanced_journal_is_rejected_by_the_domain()
    {
        await using var company = await TestCompany.CreateAsync();

        var entry = new JournalEntry
        {
            CompanyId = company.CompanyId,
            EntryDate = company.Today,
            Memo = "Broken entry",
            Source = JournalSource.Manual
        };
        entry.Lines.Add(new JournalLine
        {
            CompanyId = company.CompanyId,
            AccountId = (await company.AccountAsync("1010")).Id,
            Debit = 100m,
            LineNumber = 1
        });
        entry.Lines.Add(new JournalLine
        {
            CompanyId = company.CompanyId,
            AccountId = (await company.AccountAsync("4000")).Id,
            Credit = 90m,
            LineNumber = 2
        });

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            company.Service<IJournalPoster>().PostAsync(entry));
    }

    [Fact]
    public async Task Closed_period_blocks_posting()
    {
        await using var company = await TestCompany.CreateAsync();
        var calendar = company.Service<FiscalCalendarService>();
        var period = await calendar.GetOrCreatePeriodAsync(company.CompanyId, company.Today);
        await calendar.ClosePeriodAsync(company.CompanyId, period.Id);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            company.PostJournalAsync("Too late",
                ("1010", 10m, 0m),
                ("4000", 0m, 10m)));
    }

    [Fact]
    public async Task Lock_date_blocks_posting()
    {
        await using var company = await TestCompany.CreateAsync();
        await company.Service<CompanyService>()
            .SetLockDateAsync(company.CompanyId, company.Today);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            company.PostJournalAsync("Locked out",
                ("1010", 10m, 0m),
                ("4000", 0m, 10m)));
    }

    [Fact]
    public async Task Reversal_nets_the_original_entry_to_zero()
    {
        await using var company = await TestCompany.CreateAsync();
        var original = await company.PostJournalAsync("Accrual",
            ("6300", 900m, 0m),
            ("2300", 0m, 900m));

        await company.Service<JournalService>()
            .ReverseAsync(company.CompanyId, original.Id, company.Today, "Reversed accrual");

        Assert.Equal(0m, await company.AccountBalanceAsync("6300"));
        Assert.Equal(0m, await company.AccountBalanceAsync("2300"));
    }

    [Fact]
    public async Task Viewer_cannot_create_journals()
    {
        await using var company = await TestCompany.CreateAsync(UserRole.Viewer);

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            company.PostJournalAsync("Should fail",
                ("1010", 10m, 0m),
                ("4000", 0m, 10m)));
    }
}

public class SalesAndPaymentTests
{
    [Fact]
    public async Task Issuing_an_invoice_posts_receivable_revenue_and_output_tax()
    {
        await using var company = await TestCompany.CreateAsync();
        var customer = await company.CustomerAsync();

        var invoice = await company.IssueInvoiceAsync(customer.Id, 1_000m);

        Assert.Equal(1_200m, invoice.Total);
        Assert.Equal(1_000m, invoice.Subtotal);
        Assert.Equal(200m, invoice.TaxTotal);
        Assert.Equal(DocumentStatus.Issued, invoice.Status);

        Assert.Equal(1_200m, await company.AccountBalanceAsync("1100"));
        Assert.Equal(-1_000m, await company.AccountBalanceAsync("4000"));
        Assert.Equal(-200m, await company.AccountBalanceAsync("2100"));

        var trial = await company.Service<ReportingService>()
            .TrialBalanceAsync(company.CompanyId, company.Today, company.Today);
        Assert.True(trial.IsBalanced);
    }

    [Fact]
    public async Task A_partial_receipt_allocates_only_the_cash_received()
    {
        await using var company = await TestCompany.CreateAsync();
        var customer = await company.CustomerAsync();
        var invoice = await company.IssueInvoiceAsync(customer.Id, 1_000m);

        var payment = await company.ReceiveAsync(customer.Id, 400m, invoice.Id);

        Assert.Equal(400m, payment.AllocatedAmount);
        Assert.Equal(0m, payment.UnallocatedAmount);

        var refreshed = await company.Service<SalesService>().GetAsync(company.CompanyId, invoice.Id);
        Assert.NotNull(refreshed);
        Assert.Equal(800m, refreshed!.BalanceDue);
        Assert.Equal(DocumentStatus.PartiallyPaid, refreshed.Status);
    }

    [Fact]
    public async Task A_receipt_in_full_closes_the_invoice()
    {
        await using var company = await TestCompany.CreateAsync();
        var customer = await company.CustomerAsync();
        var invoice = await company.IssueInvoiceAsync(customer.Id, 500m);

        await company.ReceiveAsync(customer.Id, invoice.Total, invoice.Id);

        var refreshed = await company.Service<SalesService>().GetAsync(company.CompanyId, invoice.Id);
        Assert.NotNull(refreshed);
        Assert.Equal(0m, refreshed!.BalanceDue);
        Assert.Equal(DocumentStatus.Paid, refreshed.Status);
    }

    [Fact]
    public async Task An_unallocated_receipt_becomes_a_customer_credit()
    {
        await using var company = await TestCompany.CreateAsync();
        var customer = await company.CustomerAsync();

        var payment = await company.ReceiveAsync(customer.Id, 250m);

        Assert.Equal(0m, payment.AllocatedAmount);
        Assert.Equal(250m, payment.UnallocatedAmount);

        var credit = await company.Service<PaymentService>()
            .GetUnappliedCreditsAsync(company.CompanyId, customer.Id);
        Assert.Equal(250m, credit);
    }

    [Fact]
    public async Task Over_allocating_a_receipt_is_rejected()
    {
        await using var company = await TestCompany.CreateAsync();
        var customer = await company.CustomerAsync();
        var invoice = await company.IssueInvoiceAsync(customer.Id, 1_000m);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            company.ReceiveAsync(customer.Id, 100m, invoice.Id, allocation: 500m));
    }

    [Fact]
    public async Task Vendor_bill_posts_payable_and_expense()
    {
        await using var company = await TestCompany.CreateAsync();
        var vendor = await company.VendorAsync();
        var purchases = company.Service<PurchaseService>();

        var bill = await purchases.CreateDraftAsync(company.CompanyId, new BillEditModel
        {
            PartyId = vendor.Id,
            IssueDate = company.Today,
            DueDate = company.Today.AddDays(30),
            CurrencyCode = "USD",
            VendorInvoiceNumber = "V-100",
            Lines =
            {
                new InvoiceLineInput
                {
                    Description = "Software subscription",
                    AccountId = (await company.AccountAsync("6400")).Id,
                    Quantity = 1m,
                    UnitPrice = 300m,
                    TaxCodeId = (await company.TaxAsync("ZERO")).Id
                }
            }
        });
        await purchases.ReceiveAsync(company.CompanyId, bill.Id);

        Assert.Equal(300m, await company.AccountBalanceAsync("2000") * -1);
        Assert.Equal(300m, await company.AccountBalanceAsync("6400"));
    }
}
