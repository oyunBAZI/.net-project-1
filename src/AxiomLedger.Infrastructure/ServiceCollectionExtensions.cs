using AxiomLedger.Application;
using AxiomLedger.Application.Services;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AxiomLedger.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the accounting engine and its persistence. <paramref name="provider"/> accepts
    /// sqlite (default), postgresql or sqlserver.
    /// </summary>
    public static IServiceCollection AddAxiomLedger(this IServiceCollection services, string connectionString,
        string provider = "sqlite")
    {
        services.AddDbContext<AccountingDbContext>(options =>
        {
            switch (provider.Trim().ToLowerInvariant())
            {
                case "postgres":
                case "postgresql":
                    options.UseNpgsql(connectionString);
                    break;
                case "sqlserver":
                case "mssql":
                case "sql-server":
                    options.UseSqlServer(connectionString);
                    break;
                default:
                    options.UseSqlite(connectionString);
                    break;
            }
        });

        services.AddScoped<IAccountingDbContext>(sp => sp.GetRequiredService<AccountingDbContext>());
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<ICurrentUser>(_ => new SystemCurrentUser());

        services.AddScoped<AuditService>();
        services.AddScoped<NumberSequenceService>();
        services.AddScoped<LedgerAccountResolver>();
        services.AddScoped<ReferenceDataService>();
        services.AddScoped<FiscalCalendarService>();
        services.AddScoped<IJournalPoster, JournalPoster>();
        services.AddScoped<JournalService>();
        services.AddScoped<CompanyService>();
        services.AddScoped<SalesService>();
        services.AddScoped<PurchaseService>();
        services.AddScoped<PaymentService>();
        services.AddScoped<ReportingService>();
        services.AddScoped<BankingService>();
        services.AddScoped<PartyService>();
        services.AddScoped<IdentityService>();
        services.AddScoped<YearEndService>();
        services.AddScoped<SettingsService>();
        services.AddScoped<DemoDataService>();

        return services;
    }
}
