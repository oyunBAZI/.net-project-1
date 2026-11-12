using AxiomLedger.Application;
using AxiomLedger.Application.Models;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using AxiomLedger.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Axiom Ledger command line: administration, scripting and report exports that complement the web app.
// Run `axiomledger help` for the command list.

var parsed = CliArgs.Parse(args);
var options = parsed.Options;
var command = parsed.Positionals.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var sub = parsed.Positionals.Skip(1).FirstOrDefault()?.ToLowerInvariant();

string? Option(params string[] names)
{
    foreach (var name in names)
        if (options.TryGetValue(name, out var value)) return value;
    return null;
}

bool Flag(string name) => options.TryGetValue(name, out var value) &&
                          (value is null || value.Equals("true", StringComparison.OrdinalIgnoreCase));

if (command is "help" or "--help" or "-h" or "?")
{
    PrintHelp();
    return 0;
}

if (command == "version")
{
    Console.WriteLine($"Axiom Ledger {typeof(CliUser).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}");
    Console.WriteLine($"Runtime {Environment.Version} on {Environment.OSVersion}");
    return 0;
}

var connectionString = Option("connection", "conn") ?? Environment.GetEnvironmentVariable("AXIOMLEDGER_CONNECTION")
                       ?? "Data Source=axiomledger.db";
var provider = Option("provider") ?? Environment.GetEnvironmentVariable("AXIOMLEDGER_PROVIDER") ?? "sqlite";

var cliUser = new CliUser
{
    UserId = Option("as-user"),
    Email = Option("as-user") ?? "cli@localhost",
    DisplayName = "Command line"
};

var services = new ServiceCollection();
services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
services.AddAxiomLedger(connectionString, provider);
services.AddSingleton<ICurrentUser>(cliUser);

await using var serviceProvider = services.BuildServiceProvider();
await using var scope = serviceProvider.CreateAsyncScope();
var sp = scope.ServiceProvider;
var db = sp.GetRequiredService<AccountingDbContext>();

try
{
    await DatabaseInitializer.InitializeAsync(db);
    var companies = sp.GetRequiredService<CompanyService>();
    var identity = sp.GetRequiredService<IdentityService>();
    var settings = sp.GetRequiredService<SettingsService>();
    var journals = sp.GetRequiredService<JournalService>();
    var reports = sp.GetRequiredService<ReportingService>();
    var reference = sp.GetRequiredService<ReferenceDataService>();

    switch (command)
    {
        case "init":
        {
            Console.WriteLine($"Database ready using {provider}.");
            Console.WriteLine($"  connection : {SafeConnection(connectionString)}");
            var count = await db.Companies.CountAsync();
            Console.WriteLine($"  companies  : {count}");
            break;
        }

        case "setup":
        {
            var email = Option("email") ?? Prompt("Owner email");
            var password = Option("password") ?? Environment.GetEnvironmentVariable("AXIOMLEDGER_PASSWORD")
                ?? Prompt("Password", secret: true);
            var companyName = Option("company", "name") ?? Prompt("Company name");
            var currency = (Option("currency") ?? "USD").ToUpperInvariant();
            var industry = Option("industry") ?? "general";
            var fiscalMonth = int.TryParse(Option("fiscal-month"), out var month) ? month : 1;

            var user = await identity.FindByEmailAsync(email)
                       ?? await identity.CreateUserAsync(email, Option("display-name") ?? email.Split('@')[0], password,
                           platformAdmin: true);

            cliUser.UserId = user.Id.ToString();
            cliUser.Email = user.Email;
            cliUser.Role = UserRole.Owner;

            var company = await companies.CreateAsync(new NewCompanyRequest
            {
                Name = companyName,
                LegalName = Option("legal-name"),
                TaxId = Option("tax-id"),
                BaseCurrencyCode = currency,
                CountryCode = (Option("country") ?? "US").ToUpperInvariant(),
                FiscalYearStartMonth = fiscalMonth,
                Email = user.Email,
                Industry = industry
            }, user.Id.ToString());

            cliUser.CompanyId = company.Id;
            Console.WriteLine($"Company '{company.Name}' created ({company.Id}).");
            Console.WriteLine($"Owner '{user.Email}' can now sign in to the web app.");
            break;
        }

        case "seed-demo":
        {
            var email = Option("email");
            var user = email is null
                ? await db.Users.OrderBy(u => u.CreatedAt).FirstOrDefaultAsync()
                : await identity.FindByEmailAsync(email);
            if (user is null)
            {
                Console.Error.WriteLine("No user found. Run `axiomledger setup` first.");
                return 1;
            }

            cliUser.UserId = user.Id.ToString();
            cliUser.Email = user.Email;
            cliUser.Role = UserRole.Owner;

            var demo = sp.GetRequiredService<DemoDataService>();
            var id = await demo.CreateDemoCompanyAsync(user.Id.ToString());
            Console.WriteLine($"Demo company 'Northwind Trading Co.' created ({id}).");
            break;
        }

        case "company":
        {
            if (sub is "list" or null)
            {
                var rows = await db.Companies.AsNoTracking().OrderBy(c => c.Name)
                    .Select(c => new
                    {
                        c.Id, c.Name, c.BaseCurrencyCode, c.CountryCode,
                        Users = db.CompanyMemberships.Count(m => m.CompanyId == c.Id && m.IsActive),
                        Accounts = db.Accounts.Count(a => a.CompanyId == c.Id)
                    }).ToListAsync();
                Console.WriteLine($"{"ID",-38} {"NAME",-32} {"CCY",-4} {"USERS",5} {"ACCOUNTS",8}");
                foreach (var row in rows)
                    Console.WriteLine($"{row.Id,-38} {Trim(row.Name, 32),-32} {row.BaseCurrencyCode,-4} {row.Users,5} {row.Accounts,8}");
                if (rows.Count == 0) Console.WriteLine("No companies yet. Run `axiomledger setup`.");
                break;
            }
            Console.Error.WriteLine($"Unknown company command '{sub}'.");
            return 1;
        }

        case "users":
        {
            if (sub is "list" or null)
            {
                var users = await identity.ListUsersAsync();
                Console.WriteLine($"{"EMAIL",-34} {"NAME",-24} {"ACTIVE",-6} {"COMPANIES",9} {"LAST SIGN-IN",-16}");
                foreach (var user in users)
                    Console.WriteLine($"{user.Email,-34} {Trim(user.DisplayName, 24),-24} {user.IsActive,-6} {user.CompanyCount,9} {user.LastLoginAt?.ToString("yyyy-MM-dd HH:mm") ?? "never",-16}");
                break;
            }

            if (sub == "create")
            {
                var email = Option("email") ?? Prompt("Email");
                var password = Option("password") ?? Prompt("Password", secret: true);
                var user = await identity.CreateUserAsync(email, Option("name") ?? email, password, Flag("admin"));
                Console.WriteLine($"User {user.Email} created ({user.Id}).");
                break;
            }

            if (sub == "password")
            {
                var email = Option("email") ?? Prompt("Email");
                var user = await identity.FindByEmailAsync(email) ?? throw new NotFoundException("User not found.");
                var password = Option("password") ?? Prompt("New password", secret: true);
                await identity.ResetPasswordAsync(user.Id, password);
                Console.WriteLine($"Password reset for {user.Email}.");
                break;
            }

            Console.Error.WriteLine($"Unknown users command '{sub}'.");
            return 1;
        }

        case "accounts":
        {
            var company = await ResolveCompanyAsync(db, Option("company"));
            cliUser.CompanyId = company.Id;
            var accounts = await settings.ListAccountsAsync(company.Id);
            Console.WriteLine($"{"CODE",-8} {"TYPE",-14} {"GROUP",-22} {"NAME",-32} ACTIVE");
            foreach (var account in accounts.OrderBy(a => a.Code))
                Console.WriteLine($"{account.Code,-8} {account.Type,-14} {account.Group,-22} {Trim(account.Name, 32),-32} {account.IsActive}");
            break;
        }

        case "journal":
        {
            var company = await ResolveCompanyAsync(db, Option("company"));
            await AssumeRoleAsync(db, cliUser, company.Id, Option("as-user"));
            var filter = new JournalFilter
            {
                Search = Option("search"),
                From = ParseDate(Option("from")),
                To = ParseDate(Option("to")),
                Page = 1,
                PageSize = int.TryParse(Option("limit"), out var limit) ? limit : 25
            };
            var result = await journals.ListAsync(company.Id, filter);
            Console.WriteLine($"{company.Name} — {result.Total} journal(s)");
            Console.WriteLine($"{"NUMBER",-14} {"DATE",-12} {"SOURCE",-14} {"STATUS",-12} {"DEBIT",14}  MEMO");
            foreach (var row in result.Items)
                Console.WriteLine($"{row.Number,-14} {row.Date:yyyy-MM-dd} {row.Source,-14} {row.Status,-12} {row.TotalDebit,14:N2}  {Trim(row.Memo, 48)}");
            break;
        }

        case "report":
        {
            var company = await ResolveCompanyAsync(db, Option("company"));
            await AssumeRoleAsync(db, cliUser, company.Id, Option("as-user"));
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var from = ParseDate(Option("from")) ?? new DateOnly(today.Year, 1, 1);
            var to = ParseDate(Option("to")) ?? today;
            var csv = Flag("csv");
            var currency = company.BaseCurrencyCode;

            switch (sub)
            {
                case "trial-balance":
                {
                    var report = await reports.TrialBalanceAsync(company.Id, from, to, Flag("include-zero"));
                    if (csv)
                    {
                        WriteCsv(["Code", "Account", "Type", "Opening", "Debit", "Credit", "Closing"],
                            report.Rows.Select(r => new object?[]
                                { r.Code, r.Name, r.Type.ToString(), r.OpeningBalance, r.Debit, r.Credit, r.ClosingBalance }));
                        break;
                    }
                    Console.WriteLine($"{company.Name} — Trial balance {from:yyyy-MM-dd} to {to:yyyy-MM-dd} ({currency})");
                    Console.WriteLine($"{"CODE",-8} {"ACCOUNT",-40} {"OPENING",12} {"DEBIT",12} {"CREDIT",12} {"CLOSING",12}");
                    foreach (var row in report.Rows)
                        Console.WriteLine($"{row.Code,-8} {Trim(row.Name, 40),-40} {row.OpeningBalance,12:N2} {row.Debit,12:N2} {row.Credit,12:N2} {row.ClosingBalance,12:N2}");
                    Console.WriteLine(new string('-', 104));
                    Console.WriteLine($"{"TOTAL",-49} {report.OpeningTotal,12:N2} {report.TotalDebit,12:N2} {report.TotalCredit,12:N2} {report.ClosingTotal,12:N2}");
                    Console.WriteLine(report.IsBalanced ? "Books are balanced." : "WARNING: books are out of balance.");
                    break;
                }

                case "profit-loss":
                {
                    var report = await reports.ProfitAndLossAsync(company.Id, from, to);
                    Console.WriteLine($"{company.Name} — Profit & loss {from:yyyy-MM-dd} to {to:yyyy-MM-dd} ({currency})");
                    foreach (var section in report.Sections)
                    {
                        Console.WriteLine();
                        Console.WriteLine(section.Title.ToUpperInvariant());
                        foreach (var row in section.Rows)
                            Console.WriteLine($"  {row.Code,-8} {Trim(row.Label, 44),-44} {row.Amount,14:N2}");
                        Console.WriteLine($"  {"",8} {Trim("Total " + section.Title, 44),-44} {section.Total,14:N2}");
                    }
                    Console.WriteLine();
                    Console.WriteLine($"{"Revenue",-54} {report.Revenue,14:N2}");
                    Console.WriteLine($"{"Cost of sales",-54} {report.CostOfSales,14:N2}");
                    Console.WriteLine($"{"Gross profit",-54} {report.GrossProfit,14:N2}");
                    Console.WriteLine($"{"Operating expenses",-54} {report.OperatingExpenses,14:N2}");
                    Console.WriteLine($"{"Net profit",-54} {report.NetProfit,14:N2}");
                    break;
                }

                case "balance-sheet":
                {
                    var report = await reports.BalanceSheetAsync(company.Id, to);
                    Console.WriteLine($"{company.Name} — Balance sheet as of {to:yyyy-MM-dd} ({currency})");
                    foreach (var section in report.Sections)
                    {
                        Console.WriteLine();
                        Console.WriteLine(section.Title.ToUpperInvariant());
                        foreach (var row in section.Rows)
                            Console.WriteLine($"  {row.Code,-8} {Trim(row.Label, 44),-44} {row.Amount,14:N2}");
                        Console.WriteLine($"  {"",8} {Trim("Total " + section.Title, 44),-44} {section.Total,14:N2}");
                    }
                    Console.WriteLine();
                    Console.WriteLine($"{"Total assets",-54} {report.TotalAssets,14:N2}");
                    Console.WriteLine($"{"Total liabilities",-54} {report.TotalLiabilities,14:N2}");
                    Console.WriteLine($"{"Total equity",-54} {report.TotalEquity,14:N2}");
                    Console.WriteLine(report.IsBalanced ? "Balance sheet is in balance." : $"OUT OF BALANCE by {report.Difference:N2}");
                    break;
                }

                case "cash-flow":
                {
                    var report = await reports.CashFlowAsync(company.Id, from, to);
                    Console.WriteLine($"{company.Name} — Cash flow {from:yyyy-MM-dd} to {to:yyyy-MM-dd} ({currency})");
                    foreach (var section in report.Sections)
                    {
                        Console.WriteLine();
                        Console.WriteLine(section.Title.ToUpperInvariant());
                        foreach (var row in section.Rows)
                            Console.WriteLine($"  {Trim(row.Label, 50),-50} {row.Amount,14:N2}");
                        Console.WriteLine($"  {Trim("Net " + section.Title, 50),-50} {section.Total,14:N2}");
                    }
                    Console.WriteLine();
                    Console.WriteLine($"{"Opening cash",-50} {report.OpeningCash,14:N2}");
                    Console.WriteLine($"{"Net cash flow",-50} {report.NetCashFlow,14:N2}");
                    Console.WriteLine($"{"Closing cash",-50} {report.ClosingCash,14:N2}");
                    break;
                }

                case "aging":
                case "receivables-aging":
                case "payables-aging":
                {
                    var receivable = sub != "payables-aging";
                    var report = receivable
                        ? await reports.ReceivablesAgingAsync(company.Id, to)
                        : await reports.PayablesAgingAsync(company.Id, to);
                    if (csv)
                    {
                        WriteCsv(["Code", "Name", "Current", "1-30", "31-60", "61-90", "90+", "Total"],
                            report.Rows.Select(r => new object?[]
                                { r.Code, r.Name, r.Current, r.Days1To30, r.Days31To60, r.Days61To90, r.Over90, r.Total }));
                        break;
                    }
                    Console.WriteLine($"{company.Name} — {(receivable ? "Receivables" : "Payables")} aging as of {to:yyyy-MM-dd}");
                    Console.WriteLine($"{"CODE",-10} {"NAME",-32} {"CURRENT",12} {"1-30",12} {"31-60",12} {"61-90",12} {"90+",12} {"TOTAL",12}");
                    foreach (var row in report.Rows)
                        Console.WriteLine($"{row.Code,-10} {Trim(row.Name, 32),-32} {row.Current,12:N2} {row.Days1To30,12:N2} {row.Days31To60,12:N2} {row.Days61To90,12:N2} {row.Over90,12:N2} {row.Total,12:N2}");
                    Console.WriteLine($"{"TOTAL",-43} {report.Current,12:N2} {report.Days1To30,12:N2} {report.Days31To60,12:N2} {report.Days61To90,12:N2} {report.Over90,12:N2} {report.Total,12:N2}");
                    break;
                }

                case "tax":
                {
                    var report = await reports.TaxReportAsync(company.Id, from, to);
                    Console.WriteLine($"{company.Name} — Tax summary {from:yyyy-MM-dd} to {to:yyyy-MM-dd}");
                    Console.WriteLine($"{"CODE",-10} {"NAME",-28} {"RATE",7} {"SALES TAX",14} {"PURCHASE TAX",14} {"NET",14}");
                    foreach (var row in report.Summary)
                        Console.WriteLine($"{row.Code,-10} {Trim(row.Name, 28),-28} {row.Rate,7:N2} {row.SalesTaxCollected,14:N2} {row.PurchaseTaxPaid,14:N2} {row.NetLiability,14:N2}");
                    Console.WriteLine($"Net liability: {report.NetLiability:N2}");
                    break;
                }

                case "general-ledger":
                case "gl":
                {
                    var code = Option("account") ?? throw new DomainValidationException("--account <code> is required.");
                    var account = await reference.GetAllAccountsAsync(company.Id)
                                      .ContinueWith(t => t.Result.FirstOrDefault(a => a.Code == code))
                                  ?? throw new NotFoundException($"Account {code} not found.");
                    var report = await reports.GeneralLedgerAsync(company.Id, account.Id, from, to);
                    Console.WriteLine($"{report.AccountCode} {report.AccountName} — {from:yyyy-MM-dd} to {to:yyyy-MM-dd}");
                    Console.WriteLine($"Opening balance {report.OpeningBalance:N2}");
                    foreach (var row in report.Rows)
                        Console.WriteLine($"{row.Date:yyyy-MM-dd} {row.EntryNumber,-12} {Trim(row.Memo, 34),-34} {row.Debit,12:N2} {row.Credit,12:N2} {row.Balance,12:N2}");
                    Console.WriteLine($"Closing balance {report.ClosingBalance:N2}");
                    break;
                }

                default:
                    Console.Error.WriteLine($"Unknown report '{sub}'. Try trial-balance, profit-loss, balance-sheet, cash-flow, aging, tax or general-ledger.");
                    return 1;
            }
            break;
        }

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            PrintHelp();
            return 1;
    }

    return 0;
}
catch (Exception ex) when (ex is DomainValidationException or NotFoundException or PermissionDeniedException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static string Prompt(string label, bool secret = false)
{
    Console.Write($"{label}: ");
    if (!secret) return Console.ReadLine()?.Trim() ?? string.Empty;

    var buffer = new System.Text.StringBuilder();
    ConsoleKeyInfo key;
    while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace && buffer.Length > 0)
        {
            buffer.Length--;
            continue;
        }
        if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
    }
    Console.WriteLine();
    return buffer.ToString();
}

static DateOnly? ParseDate(string? value) =>
    DateOnly.TryParse(value, out var date) ? date : null;

static string Trim(string value, int length) =>
    value.Length <= length ? value : value[..(length - 1)] + "\u2026";

static string SafeConnection(string connection) =>
    connection.Length <= 120 ? connection : connection[..120] + "\u2026";

static void WriteCsv(IEnumerable<string> headers, IEnumerable<object?[]> rows) =>
    Console.Write(CsvWriter.Write(headers, rows));

static async Task<Company> ResolveCompanyAsync(AccountingDbContext db, string? key)
{
    if (!string.IsNullOrWhiteSpace(key))
    {
        if (Guid.TryParse(key, out var id))
            return await db.Companies.FindAsync(id) ?? throw new NotFoundException($"Company {key} not found.");
        var byName = await db.Companies.FirstOrDefaultAsync(c => c.Name.ToLower() == key.ToLower())
                     ?? await db.Companies.FirstOrDefaultAsync(c => c.Name.ToLower().Contains(key.ToLower()));
        return byName ?? throw new NotFoundException($"Company '{key}' not found.");
    }

    var only = await db.Companies.OrderBy(c => c.CreatedAt).FirstOrDefaultAsync()
               ?? throw new NotFoundException("No companies yet. Run `axiomledger setup` or `seed-demo`.");
    return only;
}

static async Task AssumeRoleAsync(AccountingDbContext db, CliUser user, Guid companyId, string? email)
{
    user.CompanyId = companyId;
    var membershipQuery = db.CompanyMemberships.Where(m => m.CompanyId == companyId && m.IsActive);
    CompanyMembership? membership;
    if (email is not null)
    {
        var normalized = email.Trim().ToLowerInvariant();
        membership = await membershipQuery
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m, u })
            .Where(x => x.u.Email == normalized)
            .Select(x => x.m)
            .FirstOrDefaultAsync()
            ?? throw new NotFoundException($"'{email}' is not a member of this company.");
        user.UserId = membership.UserId.ToString();
        user.Email = normalized;
    }
    else
    {
        membership = await membershipQuery.OrderBy(m => m.CreatedAt).FirstOrDefaultAsync();
        if (membership is not null) user.UserId ??= membership.UserId.ToString();
    }
    user.Role = membership?.Role ?? UserRole.Owner;
}

static void PrintHelp()
{
    Console.WriteLine("""
Axiom Ledger — cross-platform accounting suite

Usage: axiomledger <command> [options]

Commands
  init                        Create the database schema and seed currencies
  setup                       Create the first owner and a company
      --company <name> --email <owner> --password <pwd>
      [--currency USD] [--country US] [--fiscal-month 1] [--industry retail]
      [--legal-name ...] [--tax-id ...]
  seed-demo                   Seed the Northwind demo company
      [--email <owner email>]
  company list                List companies
  users list                  List platform users
  users create                Create a user
      --email <email> [--name ...] [--password ...] [--admin]
  users password              Reset a password
      --email <email> [--password ...]
  accounts --company <name>   List the chart of accounts
  journal --company <name>    List journals
      [--from yyyy-MM-dd] [--to ...] [--search ...] [--limit 25]
  report <type> --company <name>
      trial-balance | profit-loss | balance-sheet | cash-flow |
      aging | receivables-aging | payables-aging | tax |
      general-ledger (--account <code>)
      [--from yyyy-MM-dd] [--to ...] [--csv] [--include-zero]
  version                     Print the version
  help                        Show this help

Global options
  --connection <conn>         Database connection string (env AXIOMLEDGER_CONNECTION)
  --provider <name>           sqlite | postgresql | sqlserver (env AXIOMLEDGER_PROVIDER)
  --as-user <email>           Act as a specific company member (permissions/audit)

Examples
  axiomledger setup --company "Acme Ltd" --email owner@acme.test --password "Str0ngPass!"
  axiomledger seed-demo --email owner@acme.test
  axiomledger report trial-balance --company Acme --from 2026-01-01 --to 2026-12-31
  axiomledger report profit-loss --company Acme --csv > pl.csv
""");
}

internal sealed class CliUser : ICurrentUser
{
    public string? UserId { get; set; } = "cli";
    public string? Email { get; set; } = "cli@localhost";
    public string? DisplayName { get; set; } = "Command line";
    public Guid? CompanyId { get; set; }
    public UserRole? Role { get; set; } = UserRole.Owner;
    public string? IpAddress => "cli";
    public bool IsAuthenticated => true;
}

internal sealed record CliArgs(IReadOnlyList<string> Positionals, Dictionary<string, string?> Options)
{
    public static CliArgs Parse(string[] args)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token.StartsWith("--"))
            {
                var key = token[2..];
                string? value = null;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    value = args[++i];
                }
                options[key] = value ?? "true";
            }
            else
            {
                positionals.Add(token);
            }
        }

        return new CliArgs(positionals, options);
    }
}
