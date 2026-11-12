using AxiomLedger.Application;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Infrastructure;

public sealed class AccountingDbContext : DbContext, IAccountingDbContext
{
    public AccountingDbContext(DbContextOptions<AccountingDbContext> options) : base(options) { }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CompanySettings> CompanySettings => Set<CompanySettings>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
    public DbSet<TaxCode> TaxCodes => Set<TaxCode>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<FiscalYear> FiscalYears => Set<FiscalYear>();
    public DbSet<AccountingPeriod> AccountingPeriods => Set<AccountingPeriod>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();
    public DbSet<Party> Parties => Set<Party>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<SalesInvoice> SalesInvoices => Set<SalesInvoice>();
    public DbSet<SalesInvoiceLine> SalesInvoiceLines => Set<SalesInvoiceLine>();
    public DbSet<Bill> Bills => Set<Bill>();
    public DbSet<BillLine> BillLines => Set<BillLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<BankStatementLine> BankStatementLines => Set<BankStatementLine>();
    public DbSet<BankReconciliation> BankReconciliations => Set<BankReconciliation>();
    public DbSet<User> Users => Set<User>();
    public DbSet<CompanyMembership> CompanyMemberships => Set<CompanyMembership>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Company>(entity =>
        {
            entity.HasIndex(c => c.Name);
            entity.HasOne(c => c.Settings)
                .WithOne()
                .HasForeignKey<CompanySettings>(s => s.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CompanySettings>(entity =>
        {
            entity.HasIndex(s => s.CompanyId).IsUnique();
        });

        modelBuilder.Entity<Currency>(entity =>
        {
            entity.HasKey(c => c.Code);
            entity.Property(c => c.Code).HasMaxLength(3);
        });

        modelBuilder.Entity<ExchangeRate>(entity =>
        {
            entity.HasIndex(r => new { r.CompanyId, r.CurrencyCode, r.RateDate }).IsUnique();
            entity.Property(r => r.Rate).HasPrecision(18, 8);
        });

        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasIndex(a => new { a.CompanyId, a.Code }).IsUnique();
            entity.Property(a => a.Code).HasMaxLength(32);
            entity.Property(a => a.Name).HasMaxLength(160);
            entity.Ignore(a => a.NormalBalance);
            entity.HasOne<Account>().WithMany().HasForeignKey(a => a.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TaxCode>(entity =>
        {
            entity.HasIndex(t => new { t.CompanyId, t.Code }).IsUnique();
            entity.Property(t => t.Rate).HasPrecision(9, 4);
        });

        modelBuilder.Entity<Party>(entity =>
        {
            entity.HasIndex(p => new { p.CompanyId, p.Code }).IsUnique();
            entity.HasIndex(p => new { p.CompanyId, p.Name });
        });

        modelBuilder.Entity<FiscalYear>(entity =>
        {
            entity.HasIndex(f => new { f.CompanyId, f.Year }).IsUnique();
        });

        modelBuilder.Entity<AccountingPeriod>(entity =>
        {
            entity.HasIndex(p => new { p.CompanyId, p.StartDate, p.EndDate });
        });

        modelBuilder.Entity<DocumentSequence>(entity =>
        {
            entity.HasIndex(s => new { s.CompanyId, s.Key }).IsUnique();
        });

        modelBuilder.Entity<JournalEntry>(entity =>
        {
            entity.HasIndex(j => new { j.CompanyId, j.Number }).IsUnique();
            entity.HasIndex(j => new { j.CompanyId, j.EntryDate });
            entity.HasIndex(j => new { j.CompanyId, j.Status });
            entity.HasMany(j => j.Lines)
                .WithOne(l => l.JournalEntry!)
                .HasForeignKey(l => l.JournalEntryId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<JournalEntry>().WithMany()
                .HasForeignKey(j => j.ReversalOfId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<JournalEntry>().WithMany()
                .HasForeignKey(j => j.ReversedById).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JournalLine>(entity =>
        {
            entity.HasIndex(l => l.AccountId);
            entity.HasIndex(l => l.PartyId);
            entity.Ignore(l => l.SignedAmount);
            entity.HasOne(l => l.Account).WithMany()
                .HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Party>().WithMany()
                .HasForeignKey(l => l.PartyId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Department>().WithMany()
                .HasForeignKey(l => l.DepartmentId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<TaxCode>().WithMany()
                .HasForeignKey(l => l.TaxCodeId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SalesInvoice>(entity =>
        {
            entity.HasIndex(i => new { i.CompanyId, i.Number });
            entity.HasIndex(i => new { i.CompanyId, i.Status, i.DueDate });
            entity.HasIndex(i => new { i.CompanyId, i.PartyId });
            entity.HasMany(i => i.Lines).WithOne(l => l.SalesInvoice!)
                .HasForeignKey(l => l.SalesInvoiceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(i => i.Party).WithMany()
                .HasForeignKey(i => i.PartyId).OnDelete(DeleteBehavior.Restrict);
            entity.Ignore(i => i.BalanceDue);
            entity.Ignore(i => i.IsOpen);
            entity.Ignore(i => i.IsEditable);
        });

        modelBuilder.Entity<SalesInvoiceLine>(entity =>
        {
            entity.HasIndex(l => l.AccountId);
            entity.HasOne(l => l.Account).WithMany()
                .HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(l => l.TaxCode).WithMany()
                .HasForeignKey(l => l.TaxCodeId).OnDelete(DeleteBehavior.SetNull);
            entity.Ignore(l => l.LineTotal);
        });

        modelBuilder.Entity<Bill>(entity =>
        {
            entity.HasIndex(b => new { b.CompanyId, b.Number });
            entity.HasIndex(b => new { b.CompanyId, b.Status, b.DueDate });
            entity.HasMany(b => b.Lines).WithOne(l => l.Bill!)
                .HasForeignKey(l => l.BillId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(b => b.Party).WithMany()
                .HasForeignKey(b => b.PartyId).OnDelete(DeleteBehavior.Restrict);
            entity.Ignore(b => b.BalanceDue);
            entity.Ignore(b => b.IsOpen);
            entity.Ignore(b => b.IsEditable);
        });

        modelBuilder.Entity<BillLine>(entity =>
        {
            entity.HasOne(l => l.Account).WithMany()
                .HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(l => l.TaxCode).WithMany()
                .HasForeignKey(l => l.TaxCodeId).OnDelete(DeleteBehavior.SetNull);
            entity.Ignore(l => l.LineTotal);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasIndex(p => new { p.CompanyId, p.Number }).IsUnique();
            entity.HasIndex(p => new { p.CompanyId, p.PaymentDate });
            entity.HasMany(p => p.Allocations).WithOne(a => a.Payment!)
                .HasForeignKey(a => a.PaymentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(p => p.Party).WithMany()
                .HasForeignKey(p => p.PartyId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(p => p.BankAccount).WithMany()
                .HasForeignKey(p => p.BankAccountId).OnDelete(DeleteBehavior.SetNull);
            entity.Ignore(p => p.AllocatedAmount);
            entity.Ignore(p => p.UnallocatedAmount);
        });

        modelBuilder.Entity<PaymentAllocation>(entity =>
        {
            entity.HasOne(a => a.SalesInvoice).WithMany()
                .HasForeignKey(a => a.SalesInvoiceId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(a => a.Bill).WithMany()
                .HasForeignKey(a => a.BillId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BankAccount>(entity =>
        {
            entity.HasOne(b => b.Account).WithMany()
                .HasForeignKey(b => b.AccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(b => b.StatementLines).WithOne(l => l.BankAccount!)
                .HasForeignKey(l => l.BankAccountId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BankStatementLine>(entity =>
        {
            entity.HasIndex(l => new { l.CompanyId, l.BankAccountId, l.TransactionDate });
            entity.HasIndex(l => new { l.BankAccountId, l.ExternalId }).IsUnique();
            entity.Ignore(l => l.SignedAmount);
        });

        modelBuilder.Entity<BankReconciliation>(entity =>
        {
            entity.HasOne(b => b.BankAccount).WithMany()
                .HasForeignKey(b => b.BankAccountId).OnDelete(DeleteBehavior.Cascade);
            entity.Ignore(b => b.Difference);
            entity.Ignore(b => b.IsBalanced);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.Email).IsUnique();
            entity.Ignore(u => u.IsLockedOut);
            entity.HasMany(u => u.Memberships).WithOne(m => m.User!)
                .HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CompanyMembership>(entity =>
        {
            entity.HasIndex(m => new { m.CompanyId, m.UserId }).IsUnique();
            entity.HasOne(m => m.Company).WithMany()
                .HasForeignKey(m => m.CompanyId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasIndex(a => new { a.CompanyId, a.OccurredAt });
            entity.HasIndex(a => new { a.CompanyId, a.EntityType, a.EntityId });
        });

        foreach (var property in modelBuilder.Model.GetEntityTypes()
                     .SelectMany(t => t.GetProperties())
                     .Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
        {
            property.SetPrecision(18);
            property.SetScale(6);
        }
    }
}
