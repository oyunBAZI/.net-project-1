using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;

namespace AxiomLedger.Tests;

public class ReportingTests
{
    [Fact]
    public async Task Balance_sheet_balances_after_invoicing_and_receipts()
    {
        await using var company = await TestCompany.CreateAsync();
        var customer = await company.CustomerAsync();
        var invoice = await company.IssueInvoiceAsync(customer.Id, 2_000m);
        await company.ReceiveAsync(customer.Id, 1_200m, invoice.Id);

        var report = await company.Service<ReportingService>()
            .BalanceSheetAsync(company.CompanyId, company.Today);

        Assert.True(report.IsBalanced);
        Assert.Equal(0m, report.Difference);
        Assert.Equal(report.TotalAssets, report.TotalLiabilities + report.TotalEquity);
    }

    [Fact]
    public async Task Profit_and_loss_matches_posted_revenue_and_expenses()
    {
        await using var company = await TestCompany.CreateAsync();
        await company.PostJournalAsync("Sale",
            ("1010", 5_000m, 0m),
            ("4000", 0m, 5_000m));
        await company.PostJournalAsync("Rent",
            ("6100", 1_800m, 0m),
            ("1010", 0m, 1_800m));

        var report = await company.Service<ReportingService>()
            .ProfitAndLossAsync(company.CompanyId, company.Today, company.Today);

        Assert.Equal(5_000m, report.Revenue);
        Assert.Equal(1_800m, report.OperatingExpenses);
        Assert.Equal(3_200m, report.NetProfit);
    }

    [Fact]
    public async Task Cash_flow_closing_balance_equals_the_cash_account()
    {
        await using var company = await TestCompany.CreateAsync();
        await company.PostJournalAsync("Capital",
            ("1010", 9_000m, 0m),
            ("3000", 0m, 9_000m));
        await company.PostJournalAsync("Supplies",
            ("6300", 750m, 0m),
            ("1010", 0m, 750m));

        var report = await company.Service<ReportingService>()
            .CashFlowAsync(company.CompanyId, company.Today, company.Today);
        var cash = await company.AccountBalanceAsync("1010");

        Assert.Equal(cash, report.ClosingCash);
        Assert.Equal(8_250m, report.ClosingCash);
    }

    [Fact]
    public async Task Receivables_aging_buckets_an_open_invoice()
    {
        await using var company = await TestCompany.CreateAsync();
        var customer = await company.CustomerAsync();
        await company.IssueInvoiceAsync(customer.Id, 1_000m);

        var report = await company.Service<ReportingService>()
            .ReceivablesAgingAsync(company.CompanyId, company.Today.AddDays(10));

        var row = Assert.Single(report.Rows);
        Assert.Equal(1_200m, row.Total);
        Assert.Equal(1_200m, row.Current);
    }

    [Fact]
    public async Task Dashboard_aggregates_without_provider_specific_failures()
    {
        await using var company = await TestCompany.CreateAsync();
        var customer = await company.CustomerAsync();
        await company.IssueInvoiceAsync(customer.Id, 4_000m);

        var snapshot = await company.Service<ReportingService>().DashboardAsync(company.CompanyId);

        Assert.Equal(4_800m, snapshot.ReceivableBalance);
        Assert.Equal(1, snapshot.RecentActivity.Count + snapshot.RecentInvoices.Count - 1);
        Assert.NotEmpty(snapshot.MonthlySeries);
    }

    [Fact]
    public async Task Party_and_payment_lists_expose_balances()
    {
        await using var company = await TestCompany.CreateAsync();
        var customer = await company.CustomerAsync();
        var invoice = await company.IssueInvoiceAsync(customer.Id, 800m);
        await company.ReceiveAsync(customer.Id, 300m, invoice.Id);

        var parties = await company.Service<PartyService>().ListAsync(company.CompanyId);
        var party = Assert.Single(parties);
        Assert.Equal(660m, party.Receivable);

        var payments = await company.Service<PaymentService>()
            .ListAsync(company.CompanyId, new PaymentFilter());
        var payment = Assert.Single(payments.Items);
        Assert.Equal(300m, payment.Amount);
        Assert.Equal(0m, payment.Unallocated);
    }
}

public class YearEndTests
{
    [Fact]
    public async Task Closing_the_year_transfers_profit_to_retained_earnings()
    {
        await using var company = await TestCompany.CreateAsync();
        await company.PostJournalAsync("Revenue",
            ("1010", 10_000m, 0m),
            ("4000", 0m, 10_000m));
        await company.PostJournalAsync("Expenses",
            ("6100", 4_000m, 0m),
            ("1010", 0m, 4_000m));

        var yearEnd = company.Service<YearEndService>();
        await yearEnd.CloseFiscalYearAsync(company.CompanyId, company.Today.Year);

        // Revenue and expense accounts are zeroed by the closing entry, and the profit lands in equity.
        Assert.Equal(0m, await company.AccountBalanceAsync("4000"));
        Assert.Equal(0m, await company.AccountBalanceAsync("6100"));
        Assert.Equal(-6_000m, await company.AccountBalanceAsync("3100"));

        var fiscalYear = await company.Service<FiscalCalendarService>()
            .FindYearAsync(company.CompanyId, company.Today.Year);
        Assert.NotNull(fiscalYear);
        Assert.Equal(FiscalYearStatus.Closed, fiscalYear!.Status);

        var trial = await company.Service<ReportingService>()
            .TrialBalanceAsync(company.CompanyId, company.Today, company.Today);
        Assert.True(trial.IsBalanced);
    }

    [Fact]
    public async Task Year_end_checklist_flags_draft_journals()
    {
        await using var company = await TestCompany.CreateAsync();
        await company.Service<JournalService>().CreateDraftAsync(company.CompanyId, new JournalDraftRequest
        {
            EntryDate = company.Today,
            Memo = "Unfinished draft",
            Lines =
            {
                new JournalLineInput((await company.AccountAsync("6300")).Id, "Draft", 100m, 0m),
                new JournalLineInput((await company.AccountAsync("2300")).Id, "Draft", 0m, 100m)
            }
        });

        var checks = await company.Service<YearEndService>()
            .ChecklistAsync(company.CompanyId, company.Today.Year);

        var draftCheck = Assert.Single(checks, c => c.Name == "Draft journals");
        Assert.Equal("Action", draftCheck.Status);
        Assert.True(draftCheck.IsBlocking);
    }

    [Fact]
    public async Task Closing_a_year_with_drafts_is_refused()
    {
        await using var company = await TestCompany.CreateAsync();
        await company.Service<JournalService>().CreateDraftAsync(company.CompanyId, new JournalDraftRequest
        {
            EntryDate = company.Today,
            Memo = "Unfinished draft",
            Lines =
            {
                new JournalLineInput((await company.AccountAsync("6300")).Id, "Draft", 100m, 0m),
                new JournalLineInput((await company.AccountAsync("2300")).Id, "Draft", 0m, 100m)
            }
        });

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            company.Service<YearEndService>().CloseFiscalYearAsync(company.CompanyId, company.Today.Year));
    }
}
