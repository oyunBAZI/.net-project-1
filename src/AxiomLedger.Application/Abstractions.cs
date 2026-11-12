using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application;

/// <summary>
/// Persistence boundary for the application layer. Implemented by the EF Core context so services stay
/// unit-testable and the domain remains free of infrastructure concerns.
/// </summary>
public interface IAccountingDbContext
{
    DbSet<Company> Companies { get; }
    DbSet<CompanySettings> CompanySettings { get; }
    DbSet<Currency> Currencies { get; }
    DbSet<ExchangeRate> ExchangeRates { get; }
    DbSet<TaxCode> TaxCodes { get; }
    DbSet<Account> Accounts { get; }
    DbSet<FiscalYear> FiscalYears { get; }
    DbSet<AccountingPeriod> AccountingPeriods { get; }
    DbSet<Department> Departments { get; }
    DbSet<DocumentSequence> DocumentSequences { get; }
    DbSet<Party> Parties { get; }
    DbSet<JournalEntry> JournalEntries { get; }
    DbSet<JournalLine> JournalLines { get; }
    DbSet<SalesInvoice> SalesInvoices { get; }
    DbSet<SalesInvoiceLine> SalesInvoiceLines { get; }
    DbSet<Bill> Bills { get; }
    DbSet<BillLine> BillLines { get; }
    DbSet<Payment> Payments { get; }
    DbSet<PaymentAllocation> PaymentAllocations { get; }
    DbSet<BankAccount> BankAccounts { get; }
    DbSet<BankStatementLine> BankStatementLines { get; }
    DbSet<BankReconciliation> BankReconciliations { get; }
    DbSet<User> Users { get; }
    DbSet<CompanyMembership> CompanyMemberships { get; }
    DbSet<AuditLog> AuditLogs { get; }

    Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry Entry(object entity);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IClock
{
    DateOnly Today { get; }
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>
/// Serialises document-number allocation per company inside a single service operation.
/// The accounting invariant (no duplicate journal or invoice numbers) is additionally protected by
/// unique database indexes, so multi-instance deployments fail safely rather than double-numbering.
/// </summary>
public sealed class CompanyLock
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    public static async Task<IDisposable> AcquireAsync(Guid companyId, CancellationToken ct = default)
    {
        var gate = Locks.GetOrAdd(companyId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;
        public Releaser(SemaphoreSlim gate) => _gate = gate;
        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            gate?.Release();
        }
    }
}

/// <summary>PBKDF2-SHA256 password hashing with per-user salt and iteration count.</summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    public const int DefaultIterations = 210_000;

    public static (string hash, string salt, int iterations) Hash(string password, int iterations = DefaultIterations)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new DomainValidationException("A password is required.");
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(SaltSize);
        var key = Derive(password, salt, iterations);
        return (Convert.ToBase64String(key), Convert.ToBase64String(salt), iterations);
    }

    public static bool Verify(string password, string? hash, string? salt, int iterations)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt) || iterations <= 0) return false;
        try
        {
            var expected = Convert.FromBase64String(hash);
            var actual = Derive(password, Convert.FromBase64String(salt), iterations);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool NeedsRehash(int iterations) => iterations < DefaultIterations;

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, KeySize);
}
