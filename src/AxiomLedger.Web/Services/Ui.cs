using AxiomLedger.Domain;

namespace AxiomLedger.Web.Services;

/// <summary>Presentation helpers shared by Razor views.</summary>
public static class Ui
{
    public static string Money(decimal value, string? currency = "USD") =>
        Domain.Money.Format(value, currency ?? "USD");

    public static string Number(decimal value) => value.ToString("N2");

    public static string Date(DateOnly? value) => value?.ToString("dd MMM yyyy") ?? "\u2014";

    public static string Date(DateOnly value) => value.ToString("dd MMM yyyy");

    public static string DateInput(DateOnly? value) => value?.ToString("yyyy-MM-dd") ?? string.Empty;

    public static string DateInput(DateOnly value) => value.ToString("yyyy-MM-dd");

    public static string DaysAgo(DateOnly date) => date == default ? string.Empty : Ui.Relative(date);

    public static string Relative(DateOnly date)
    {
        var days = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - date.DayNumber;
        return days switch
        {
            0 => "Today",
            1 => "Yesterday",
            > 1 and < 7 => $"{days} days ago",
            > 7 => date.ToString("dd MMM"),
            -1 => "Tomorrow",
            < -1 => date.ToString("dd MMM"),
            _ => string.Empty
        };
    }

    public static string Status(JournalStatus status) => status switch
    {
        JournalStatus.Draft => "badge-neutral",
        JournalStatus.PendingApproval => "badge-warning",
        JournalStatus.Posted => "badge-success",
        JournalStatus.Reversed => "badge-muted",
        JournalStatus.Void => "badge-danger",
        _ => "badge-neutral"
    };

    public static string Status(DocumentStatus status) => status switch
    {
        DocumentStatus.Draft => "badge-neutral",
        DocumentStatus.Issued => "badge-info",
        DocumentStatus.PartiallyPaid => "badge-warning",
        DocumentStatus.Paid => "badge-success",
        DocumentStatus.Void => "badge-danger",
        _ => "badge-neutral"
    };

    public static string Status(PeriodStatus status) => status switch
    {
        PeriodStatus.Open => "badge-success",
        PeriodStatus.SoftClosed => "badge-warning",
        PeriodStatus.Closed => "badge-muted",
        _ => "badge-neutral"
    };

    public static string Source(JournalSource source) => source switch
    {
        JournalSource.Manual => "Manual",
        JournalSource.OpeningBalance => "Opening balance",
        JournalSource.SalesInvoice => "Sales invoice",
        JournalSource.SalesCreditNote => "Credit note",
        JournalSource.PurchaseBill => "Vendor bill",
        JournalSource.PurchaseCreditNote => "Bill credit",
        JournalSource.CustomerReceipt => "Customer receipt",
        JournalSource.VendorPayment => "Vendor payment",
        JournalSource.BankImport => "Bank import",
        JournalSource.Depreciation => "Depreciation",
        JournalSource.Payroll => "Payroll",
        JournalSource.YearEndClose => "Year-end close",
        JournalSource.FxRevaluation => "FX revaluation",
        _ => source.ToString()
    };

    public static string Role(UserRole role) => role.ToString();

    public static string AccountType(AccountType type) => type switch
    {
        Domain.AccountType.Asset => "Asset",
        Domain.AccountType.Liability => "Liability",
        Domain.AccountType.Equity => "Equity",
        Domain.AccountType.Revenue => "Revenue",
        Domain.AccountType.Expense => "Expense",
        _ => type.ToString()
    };

    public static string Group(AccountGroup group) => group switch
    {
        AccountGroup.CurrentAsset => "Current asset",
        AccountGroup.FixedAsset => "Fixed asset",
        AccountGroup.OtherAsset => "Other asset",
        AccountGroup.CurrentLiability => "Current liability",
        AccountGroup.NonCurrentLiability => "Non-current liability",
        AccountGroup.Equity => "Equity",
        AccountGroup.Revenue => "Revenue",
        AccountGroup.CostOfSales => "Cost of sales",
        AccountGroup.OperatingExpense => "Operating expense",
        AccountGroup.OtherIncome => "Other income",
        AccountGroup.OtherExpense => "Other expense",
        _ => "Unclassified"
    };

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1
            ? parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant()
            : $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    public static string Signed(decimal value) => (value > 0 ? "+" : string.Empty) + value.ToString("N2");
}
