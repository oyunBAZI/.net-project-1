using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AxiomLedger.Infrastructure;

/// <summary>Creates the schema and seeds global reference data. Safe to call on every start-up.</summary>
public static class DatabaseInitializer
{
    private static readonly (string Code, string Name, string Symbol)[] Currencies =
    {
        ("USD", "US Dollar", "$"),
        ("EUR", "Euro", "\u20ac"),
        ("GBP", "British Pound", "\u00a3"),
        ("JPY", "Japanese Yen", "\u00a5"),
        ("CHF", "Swiss Franc", "CHF"),
        ("CAD", "Canadian Dollar", "C$"),
        ("AUD", "Australian Dollar", "A$"),
        ("NZD", "New Zealand Dollar", "NZ$"),
        ("CNY", "Chinese Yuan", "\u00a5"),
        ("INR", "Indian Rupee", "\u20b9"),
        ("SGD", "Singapore Dollar", "S$"),
        ("HKD", "Hong Kong Dollar", "HK$"),
        ("SEK", "Swedish Krona", "kr"),
        ("NOK", "Norwegian Krone", "kr"),
        ("DKK", "Danish Krone", "kr"),
        ("PLN", "Polish Zloty", "z\u0142"),
        ("CZK", "Czech Koruna", "K\u010d"),
        ("TRY", "Turkish Lira", "\u20ba"),
        ("BRL", "Brazilian Real", "R$"),
        ("MXN", "Mexican Peso", "MX$"),
        ("ZAR", "South African Rand", "R"),
        ("NGN", "Nigerian Naira", "\u20a6"),
        ("KES", "Kenyan Shilling", "KSh"),
        ("AED", "UAE Dirham", "AED"),
        ("SAR", "Saudi Riyal", "SAR"),
        ("ILS", "Israeli Shekel", "\u20aa"),
        ("KRW", "South Korean Won", "\u20a9")
    };

    public static async Task InitializeAsync(AccountingDbContext db, ILogger? logger = null,
        CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);

        var existing = await db.Currencies.Select(c => c.Code).ToListAsync(ct);
        var added = 0;
        foreach (var (code, name, symbol) in Currencies)
        {
            if (existing.Contains(code)) continue;
            db.Currencies.Add(new Currency { Code = code, Name = name, Symbol = symbol, DecimalPlaces = 2, IsActive = true });
            added++;
        }
        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            logger?.LogInformation("Seeded {Count} currencies", added);
        }
    }

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var logger = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(nameof(DatabaseInitializer));
        await InitializeAsync(db, logger, ct);
    }
}
