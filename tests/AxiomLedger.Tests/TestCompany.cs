using AxiomLedger.Application;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AxiomLedger.Tests;

/// <summary>
/// A disposable, fully initialised company backed by a throwaway SQLite file. Every test gets its own
/// database, user and chart of accounts so tests can run in parallel without interference.
/// </summary>
public sealed class TestCompany : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;
    private readonly string _path;

    public TestUser User { get; }
    public Guid CompanyId { get; }
    public Guid UserId { get; }
    public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private TestCompany(ServiceProvider provider, AsyncServiceScope scope, string path, TestUser user,
        Guid companyId, Guid userId)
    {
        _provider = provider;
        _scope = scope;
        _path = path;
        User = user;
        CompanyId = companyId;
        UserId = userId;
    }

    public T Service<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();
    public IAccountingDbContext Db => Service<IAccountingDbContext>();

    public static async Task<TestCompany> CreateAsync(UserRole role = UserRole.Owner)
    {
        var path = Path.Combine(Path.GetTempPath(), $"axiom-test-{Guid.NewGuid():N}.db");
        var user = new TestUser();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAxiomLedger($"Data Source={path}", "sqlite");
        services.AddSingleton<ICurrentUser>(user);

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        await DatabaseInitializer.InitializeAsync(db);

        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        var companies = scope.ServiceProvider.GetRequiredService<CompanyService>();

        var owner = await identity.CreateUserAsync("owner@test.local", "Test Owner", "Str0ngPass!123");
        var company = await companies.CreateAsync(new NewCompanyRequest
        {
            Name = "Test Company",
            LegalName = "Test Company LLC",
            BaseCurrencyCode = "USD",
            CountryCode = "US",
            FiscalYearStartMonth = 1,
            Industry = "general"
        }, owner.Id.ToString());

        user.UserId = owner.Id.ToString();
        user.Email = owner.Email;
        user.CompanyId = company.Id;
        user.Role = role;

        return new TestCompany(provider, scope, path, user, company.Id, owner.Id);
    }

    public async Task<Account> AccountAsync(string code) =>
        await Db.Accounts.FirstAsync(a => a.CompanyId == CompanyId && a.Code == code);

    public async Task<TaxCode> TaxAsync(string code) =>
        await Db.TaxCodes.FirstAsync(t => t.CompanyId == CompanyId && t.Code == code);

    public Task<Party> CustomerAsync(string name = "Acme Customer") =>
        Service<PartyService>().CreateAsync(CompanyId, new PartyRequest
        {
            Type = PartyType.Customer,
            Name = name,
            CurrencyCode = "USD",
            PaymentTermsDays = 30
        });

    public Task<Party> VendorAsync(string name = "Acme Vendor") =>
        Service<PartyService>().CreateAsync(CompanyId, new PartyRequest
        {
            Type = PartyType.Vendor,
            Name = name,
            CurrencyCode = "USD",
            PaymentTermsDays = 30
        });

    /// <summary>Creates and posts a balanced manual journal from account code / debit / credit triples.</summary>
    public async Task<JournalEntry> PostJournalAsync(string memo, params (string Code, decimal Debit, decimal Credit)[] lines)
    {
        var journal = Service<JournalService>();
        var request = new JournalDraftRequest { EntryDate = Today, Memo = memo };
        foreach (var (code, debit, credit) in lines)
            request.Lines.Add(new JournalLineInput((await AccountAsync(code)).Id, memo, debit, credit));

        var draft = await journal.CreateDraftAsync(CompanyId, request);
        await journal.PostAsync(CompanyId, draft.Id);
        return draft;
    }

    public async Task<SalesInvoice> IssueInvoiceAsync(Guid customerId, decimal unitPrice, decimal quantity = 1m,
        string revenueAccount = "4000", string taxCode = "TAX20")
    {
        var sales = Service<SalesService>();
        var invoice = await sales.CreateDraftAsync(CompanyId, new InvoiceEditModel
        {
            PartyId = customerId,
            IssueDate = Today,
            DueDate = Today.AddDays(30),
            CurrencyCode = "USD",
            Lines =
            {
                new InvoiceLineInput
                {
                    Description = "Consulting services",
                    AccountId = (await AccountAsync(revenueAccount)).Id,
                    Quantity = quantity,
                    UnitPrice = unitPrice,
                    TaxCodeId = (await TaxAsync(taxCode)).Id
                }
            }
        });
        return await sales.IssueAsync(CompanyId, invoice.Id);
    }

    public Task<Payment> ReceiveAsync(Guid customerId, decimal amount, Guid? invoiceId = null,
        decimal? allocation = null) =>
        Service<PaymentService>().RecordAsync(CompanyId, new PaymentRequest
        {
            PartyId = customerId,
            Direction = PaymentDirection.Receipt,
            PaymentDate = Today,
            Amount = amount,
            CurrencyCode = "USD",
            Method = PaymentMethod.BankTransfer,
            ContraAccountId = AccountAsync("1010").GetAwaiter().GetResult().Id,
            Allocations = invoiceId is null
                ? new List<PaymentAllocationInput>()
                : new List<PaymentAllocationInput>
                {
                    new() { SalesInvoiceId = invoiceId, Amount = allocation }
                }
        });

    public async Task<decimal> AccountBalanceAsync(string code)
    {
        var id = (await AccountAsync(code)).Id;
        var lines = await Db.JournalLines
            .Where(l => l.AccountId == id && l.JournalEntry!.Status == JournalStatus.Posted)
            .Select(l => new { l.Debit, l.Credit })
            .ToListAsync();
        return Money.Round(lines.Sum(l => l.Debit - l.Credit));
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (IOException)
        {
            // The OS still holds the handle; the temporary file will be reclaimed with the temp directory.
        }
    }
}

public sealed class TestUser : ICurrentUser
{
    public string? UserId { get; set; } = "test-user";
    public string? Email { get; set; } = "test@localhost";
    public string? DisplayName { get; set; } = "Test User";
    public Guid? CompanyId { get; set; }
    public UserRole? Role { get; set; } = UserRole.Owner;
    public string? IpAddress { get; set; } = "127.0.0.1";
    public bool IsAuthenticated => true;
}
