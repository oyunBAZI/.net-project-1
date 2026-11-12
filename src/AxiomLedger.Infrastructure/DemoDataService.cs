using System.Globalization;
using System.Text;
using AxiomLedger.Application;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Infrastructure;

/// <summary>
/// Seeds a realistic demo company so a new installation can be explored immediately: customers, vendors,
/// invoices, bills, receipts, payments, bank transactions and an approval queue.
/// </summary>
public sealed class DemoDataService
{
    private readonly AccountingDbContext _db;
    private readonly CompanyService _companies;
    private readonly PartyService _parties;
    private readonly SalesService _sales;
    private readonly PurchaseService _purchases;
    private readonly PaymentService _payments;
    private readonly BankingService _banking;
    private readonly JournalService _journals;
    private readonly IJournalPoster _poster;
    private readonly IClock _clock;

    public DemoDataService(AccountingDbContext db, CompanyService companies, PartyService parties,
        SalesService sales, PurchaseService purchases, PaymentService payments, BankingService banking,
        JournalService journals, IJournalPoster poster, IClock clock)
    {
        _db = db;
        _companies = companies;
        _parties = parties;
        _sales = sales;
        _purchases = purchases;
        _payments = payments;
        _banking = banking;
        _journals = journals;
        _poster = poster;
        _clock = clock;
    }

    public async Task<Guid> CreateDemoCompanyAsync(string ownerUserId, CancellationToken ct = default)
    {
        var today = _clock.Today;
        var company = await _companies.CreateAsync(new NewCompanyRequest
        {
            Name = "Northwind Trading Co.",
            LegalName = "Northwind Trading Company LLC",
            TaxId = "US-84-2910556",
            BaseCurrencyCode = "USD",
            CountryCode = "US",
            FiscalYearStartMonth = 1,
            AddressLine1 = "1200 Harbor Street",
            City = "Seattle",
            Region = "WA",
            PostalCode = "98101",
            Email = "finance@northwind.example",
            Phone = "+1 206 555 0142",
            Industry = "retail"
        }, ownerUserId, ct);

        var accounts = await _db.Accounts.Where(a => a.CompanyId == company.Id)
            .ToDictionaryAsync(a => a.Code, a => a.Id, ct);
        var zeroTax = await _db.TaxCodes.FirstAsync(t => t.CompanyId == company.Id && t.Code == "ZERO", ct);

        var bank = await _banking.CreateAccountAsync(company.Id, new BankAccountRequest
        {
            Name = "Operating Account",
            BankName = "First Cascade Bank",
            AccountNumber = "\u2022\u2022\u2022\u2022 4821",
            AccountId = accounts["1010"],
            CurrencyCode = "USD"
        }, ct);

        var customers = new List<Party>();
        foreach (var (name, email) in new[]
                 {
                     ("Acme Corporation", "ap@acme.example"),
                     ("Globex Industries", "finance@globex.example"),
                     ("Initech Systems", "billing@initech.example"),
                     ("Umbrella Health", "accounts@umbrella.example"),
                     ("Stark Manufacturing", "ap@stark.example"),
                     ("Wayne Enterprises", "payables@wayne.example")
                 })
        {
            customers.Add(await _parties.CreateAsync(company.Id, new PartyRequest
            {
                Type = PartyType.Customer,
                Name = name,
                Email = email,
                CurrencyCode = "USD",
                PaymentTermsDays = 30,
                CreditLimit = 250_000m
            }, ct));
        }

        var vendors = new List<Party>();
        foreach (var (name, email) in new[]
                 {
                     ("CloudHost Services", "billing@cloudhost.example"),
                     ("Office Depot Business", "ar@officedepot.example"),
                     ("Harper & Klein LLP", "invoices@harperklein.example"),
                     ("Harbor Property Group", "rent@harborproperty.example")
                 })
        {
            vendors.Add(await _parties.CreateAsync(company.Id, new PartyRequest
            {
                Type = PartyType.Vendor,
                Name = name,
                Email = email,
                CurrencyCode = "USD",
                PaymentTermsDays = 30
            }, ct));
        }

        // Opening balances five months back.
        var openingDate = new DateOnly(today.Year, today.Month, 1).AddMonths(-5);
        var opening = new JournalEntry
        {
            CompanyId = company.Id,
            EntryDate = openingDate,
            Memo = "Opening balances",
            Reference = "OPENING",
            Source = JournalSource.OpeningBalance,
            CreatedByUserId = ownerUserId
        };
        opening.Lines.Add(new JournalLine
        {
            CompanyId = company.Id,
            AccountId = accounts["1010"],
            Description = "Opening bank balance",
            Debit = 184_500m,
            LineNumber = 1
        });
        opening.Lines.Add(new JournalLine
        {
            CompanyId = company.Id,
            AccountId = accounts["1500"],
            Description = "Office equipment",
            Debit = 42_000m,
            LineNumber = 2
        });
        opening.Lines.Add(new JournalLine
        {
            CompanyId = company.Id,
            AccountId = accounts["1200"],
            Description = "Opening inventory",
            Debit = 26_400m,
            LineNumber = 3
        });
        opening.Lines.Add(new JournalLine
        {
            CompanyId = company.Id,
            AccountId = accounts["3000"],
            Description = "Owner's capital",
            Credit = 252_900m,
            LineNumber = 4
        });
        await _poster.PostAsync(opening, ct);
        await _db.SaveChangesAsync(ct);

        // Invoices across the last five months.
        var invoiceIds = new List<(Guid Id, decimal Total, DateOnly Date, Guid CustomerId)>();
        var plans = new (int Customer, int MonthsAgo, string Description, decimal Quantity, decimal Price)[]
        {
            (0, 4, "Warehouse shelving units", 12m, 480m),
            (1, 4, "Annual maintenance contract", 1m, 12_600m),
            (2, 3, "Network hardware bundle", 4m, 1_850m),
            (3, 3, "Supply replenishment", 30m, 122m),
            (4, 2, "Custom fabrication run", 1m, 18_400m),
            (5, 2, "Logistics consulting", 16m, 245m),
            (0, 1, "Packaging materials", 40m, 68m),
            (1, 1, "Onsite support hours", 24m, 180m),
            (2, 1, "Software licences", 15m, 199m),
            (4, 0, "Precision components", 1m, 7_950m),
            (3, 0, "Seasonal stock", 22m, 310m),
            (5, 0, "Project management retainer", 1m, 4_800m)
        };

        foreach (var plan in plans)
        {
            var issueDate = new DateOnly(today.Year, today.Month, 1).AddMonths(-plan.MonthsAgo).AddDays(6);
            if (issueDate > today) issueDate = today.AddDays(-2);
            var invoice = await _sales.CreateDraftAsync(company.Id, new InvoiceEditModel
            {
                PartyId = customers[plan.Customer].Id,
                IssueDate = issueDate,
                DueDate = issueDate.AddDays(30),
                CurrencyCode = "USD",
                PurchaseOrderNumber = $"PO-{issueDate:yyyyMM}-{plan.Customer + 1:00}",
                Lines =
                {
                    new InvoiceLineInput
                    {
                        Description = plan.Description,
                        AccountId = accounts[plan.Customer % 2 == 0 ? "4000" : "4100"],
                        Quantity = plan.Quantity,
                        UnitPrice = plan.Price,
                        TaxCodeId = zeroTax.Id
                    }
                }
            }, ct);
            await _sales.IssueAsync(company.Id, invoice.Id, ct);
            invoiceIds.Add((invoice.Id, invoice.Total, invoice.IssueDate, customers[plan.Customer].Id));
        }

        // Customer receipts: most invoices paid, one partial.
        for (var i = 0; i < invoiceIds.Count; i++)
        {
            var invoice = invoiceIds[i];
            if (i % 5 == 3)
            {
                await _payments.RecordAsync(company.Id, new PaymentRequest
                {
                    PartyId = invoice.CustomerId,
                    Direction = PaymentDirection.Receipt,
                    PaymentDate = invoice.Date.AddDays(12),
                    Amount = Money.Round(invoice.Total * 0.4m),
                    CurrencyCode = "USD",
                    Method = PaymentMethod.BankTransfer,
                    BankAccountId = bank.Id,
                    Reference = $"WIRE-{7000 + i}",
                    Allocations = { new PaymentAllocationInput { SalesInvoiceId = invoice.Id } }
                }, ct);
            }
            else if (i % 5 != 4)
            {
                await _payments.RecordAsync(company.Id, new PaymentRequest
                {
                    PartyId = invoice.CustomerId,
                    Direction = PaymentDirection.Receipt,
                    PaymentDate = invoice.Date.AddDays(18),
                    Amount = invoice.Total,
                    CurrencyCode = "USD",
                    Method = PaymentMethod.BankTransfer,
                    BankAccountId = bank.Id,
                    Reference = $"WIRE-{7000 + i}",
                    Allocations = { new PaymentAllocationInput { SalesInvoiceId = invoice.Id } }
                }, ct);
            }
        }

        // Vendor bills.
        var billPlans = new (int Vendor, int MonthsAgo, string Description, string Account, decimal Amount)[]
        {
            (0, 4, "Cloud hosting \u2013 annual plan", "6400", 9_600m),
            (1, 4, "Office supplies", "6300", 1_240m),
            (2, 3, "Audit and tax advisory", "6600", 6_500m),
            (3, 3, "Warehouse rent", "6100", 7_500m),
            (0, 2, "Cloud hosting \u2013 Q3", "6400", 2_400m),
            (3, 2, "Warehouse rent", "6100", 7_500m),
            (1, 1, "Packaging and consumables", "6300", 2_180m),
            (2, 0, "Legal review", "6600", 3_200m),
            (3, 0, "Warehouse rent", "6100", 7_500m)
        };

        var billIds = new List<(Guid Id, decimal Total)>();
        for (var i = 0; i < billPlans.Length; i++)
        {
            var plan = billPlans[i];
            var billDate = new DateOnly(today.Year, today.Month, 1).AddMonths(-plan.MonthsAgo).AddDays(9);
            if (billDate > today) billDate = today.AddDays(-1);
            var bill = await _purchases.CreateDraftAsync(company.Id, new BillEditModel
            {
                PartyId = vendors[plan.Vendor].Id,
                VendorInvoiceNumber = $"V-{1000 + i}",
                IssueDate = billDate,
                DueDate = billDate.AddDays(30),
                CurrencyCode = "USD",
                Lines =
                {
                    new InvoiceLineInput
                    {
                        Description = plan.Description,
                        AccountId = accounts[plan.Account],
                        Quantity = 1m,
                        UnitPrice = plan.Amount,
                        TaxCodeId = zeroTax.Id
                    }
                }
            }, ct);
            await _purchases.ReceiveAsync(company.Id, bill.Id, ct);
            billIds.Add((bill.Id, bill.Total));
        }

        for (var i = 0; i < billIds.Count; i++)
        {
            if (i % 3 == 2) continue;
            var bill = billIds[i];
            await _payments.RecordAsync(company.Id, new PaymentRequest
            {
                PartyId = vendors[billPlans[i].Vendor].Id,
                Direction = PaymentDirection.Disbursement,
                PaymentDate = new DateOnly(today.Year, today.Month, 1).AddMonths(-billPlans[i].MonthsAgo).AddDays(20),
                Amount = bill.Total,
                CurrencyCode = "USD",
                Method = PaymentMethod.BankTransfer,
                BankAccountId = bank.Id,
                Reference = $"ACH-{4100 + i}",
                Allocations = { new PaymentAllocationInput { BillId = bill.Id } }
            }, ct);
        }

        // Bank statement covering the last two months, then auto-match.
        var since = today.AddMonths(-2);
        var bankLines = await (from l in _db.JournalLines
                               join j in _db.JournalEntries on l.JournalEntryId equals j.Id
                               where j.CompanyId == company.Id && j.Status == JournalStatus.Posted
                                     && l.AccountId == bank.AccountId && j.EntryDate >= since
                               orderby j.EntryDate, j.Number
                               select new { j.EntryDate, j.Memo, j.Reference, l.Debit, l.Credit })
            .ToListAsync(ct);

        var csv = new StringBuilder();
        csv.AppendLine("Date,Description,Reference,Amount");
        foreach (var line in bankLines.Take(40))
        {
            var amount = line.Debit - line.Credit;
            csv.AppendLine(string.Join(",",
                line.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Quote(line.Memo),
                Quote(line.Reference ?? string.Empty),
                amount.ToString("0.00", CultureInfo.InvariantCulture)));
        }

        if (bankLines.Count > 0)
        {
            await _banking.ImportCsvAsync(company.Id, bank.Id, csv.ToString(), ct);
            await _banking.AutoMatchAsync(company.Id, bank.Id, ct);
        }

        // Approval queue: one draft and one pending journal.
        var draft = await _journals.CreateDraftAsync(company.Id, new JournalDraftRequest
        {
            EntryDate = today,
            Memo = "Accrual \u2013 unused office supplies",
            Reference = "ACCR-01",
            Lines =
            {
                new JournalLineInput(accounts["6300"], "Office supplies accrual", 320m, 0m),
                new JournalLineInput(accounts["2300"], "Accrued liability", 0m, 320m)
            }
        }, ct);
        await _journals.SubmitForApprovalAsync(company.Id, draft.Id, ct);

        await _journals.CreateDraftAsync(company.Id, new JournalDraftRequest
        {
            EntryDate = today,
            Memo = "Proposed equipment purchase \u2013 awaiting documents",
            Reference = "DRAFT-02",
            Lines =
            {
                new JournalLineInput(accounts["1500"], "Forklift", 14_500m, 0m),
                new JournalLineInput(accounts["2500"], "Equipment finance", 0m, 14_500m)
            }
        }, ct);

        return company.Id;
    }

    private static string Quote(string value) =>
        value.Contains(',') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
