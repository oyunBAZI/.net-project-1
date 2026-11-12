using AxiomLedger.Domain;

namespace AxiomLedger.Application.Services;

/// <summary>
/// Account templates used when a company is created. A complete chart keeps reporting useful from day one,
/// while every account remains editable so enterprises can map their own chart.
/// </summary>
public static class ChartOfAccountsTemplate
{
    public sealed record TemplateAccount(string Code, string Name, AccountType Type, AccountGroup Group,
        bool IsCash = false, bool IsContra = false, string? Description = null);

    public static IReadOnlyList<TemplateAccount> For(string industry)
    {
        var accounts = new List<TemplateAccount>
        {
            new("1010", "Checking Account", AccountType.Asset, AccountGroup.CurrentAsset, IsCash: true, Description: "Primary operating bank account"),
            new("1020", "Savings Account", AccountType.Asset, AccountGroup.CurrentAsset, IsCash: true),
            new("1030", "Petty Cash", AccountType.Asset, AccountGroup.CurrentAsset, IsCash: true),
            new("1100", "Accounts Receivable", AccountType.Asset, AccountGroup.CurrentAsset, Description: "Amounts owed by customers"),
            new("1110", "Allowance for Doubtful Accounts", AccountType.Asset, AccountGroup.CurrentAsset, IsContra: true),
            new("1200", "Inventory", AccountType.Asset, AccountGroup.CurrentAsset),
            new("1300", "Prepaid Expenses", AccountType.Asset, AccountGroup.CurrentAsset),
            new("1400", "Tax Receivable", AccountType.Asset, AccountGroup.CurrentAsset, Description: "Input tax recoverable"),
            new("1500", "Office Equipment", AccountType.Asset, AccountGroup.FixedAsset),
            new("1510", "Accumulated Depreciation \u2013 Equipment", AccountType.Asset, AccountGroup.FixedAsset, IsContra: true),
            new("1600", "Vehicles", AccountType.Asset, AccountGroup.FixedAsset),
            new("1610", "Accumulated Depreciation \u2013 Vehicles", AccountType.Asset, AccountGroup.FixedAsset, IsContra: true),
            new("1700", "Leasehold Improvements", AccountType.Asset, AccountGroup.FixedAsset),
            new("1800", "Intangible Assets", AccountType.Asset, AccountGroup.OtherAsset),

            new("2000", "Accounts Payable", AccountType.Liability, AccountGroup.CurrentLiability, Description: "Amounts owed to vendors"),
            new("2100", "Sales Tax Payable", AccountType.Liability, AccountGroup.CurrentLiability, Description: "Output tax collected"),
            new("2200", "Payroll Liabilities", AccountType.Liability, AccountGroup.CurrentLiability),
            new("2300", "Accrued Expenses", AccountType.Liability, AccountGroup.CurrentLiability),
            new("2400", "Customer Deposits", AccountType.Liability, AccountGroup.CurrentLiability),
            new("2500", "Short-term Loans", AccountType.Liability, AccountGroup.CurrentLiability),
            new("2600", "Long-term Loans", AccountType.Liability, AccountGroup.NonCurrentLiability),

            new("3000", "Owner's Equity", AccountType.Equity, AccountGroup.Equity),
            new("3100", "Retained Earnings", AccountType.Equity, AccountGroup.Equity),
            new("3200", "Owner Drawings", AccountType.Equity, AccountGroup.Equity, IsContra: true),

            new("4000", "Product Sales", AccountType.Revenue, AccountGroup.Revenue),
            new("4100", "Service Revenue", AccountType.Revenue, AccountGroup.Revenue),
            new("4200", "Shipping Income", AccountType.Revenue, AccountGroup.Revenue),
            new("4300", "Sales Discounts", AccountType.Revenue, AccountGroup.Revenue, IsContra: true),
            new("4900", "Other Income", AccountType.Revenue, AccountGroup.OtherIncome),
            new("4950", "Foreign Exchange Gain", AccountType.Revenue, AccountGroup.OtherIncome),

            new("5000", "Cost of Goods Sold", AccountType.Expense, AccountGroup.CostOfSales),
            new("5100", "Freight & Delivery", AccountType.Expense, AccountGroup.CostOfSales),

            new("6000", "Salaries & Wages", AccountType.Expense, AccountGroup.OperatingExpense),
            new("6010", "Payroll Taxes", AccountType.Expense, AccountGroup.OperatingExpense),
            new("6100", "Rent", AccountType.Expense, AccountGroup.OperatingExpense),
            new("6200", "Utilities", AccountType.Expense, AccountGroup.OperatingExpense),
            new("6300", "Office Supplies", AccountType.Expense, AccountGroup.OperatingExpense),
            new("6400", "Software & Subscriptions", AccountType.Expense, AccountGroup.OperatingExpense),
            new("6500", "Marketing & Advertising", AccountType.Expense, AccountGroup.OperatingExpense),
            new("6600", "Professional Fees", AccountType.Expense, AccountGroup.OperatingExpense),
            new("6700", "Insurance", AccountType.Expense, AccountGroup.OperatingExpense),
            new("6800", "Bank Fees", AccountType.Expense, AccountGroup.OperatingExpense),
            new("6900", "Travel & Meals", AccountType.Expense, AccountGroup.OperatingExpense),
            new("7000", "Depreciation Expense", AccountType.Expense, AccountGroup.OperatingExpense),
            new("7100", "Repairs & Maintenance", AccountType.Expense, AccountGroup.OperatingExpense),
            new("7200", "Bad Debt Expense", AccountType.Expense, AccountGroup.OperatingExpense),
            new("7900", "Miscellaneous Expense", AccountType.Expense, AccountGroup.OperatingExpense),
            new("8000", "Interest Expense", AccountType.Expense, AccountGroup.OtherExpense),
            new("8100", "Income Tax Expense", AccountType.Expense, AccountGroup.OtherExpense),
            new("8200", "Foreign Exchange Loss", AccountType.Expense, AccountGroup.OtherExpense)
        };

        if (industry.Equals("manufacturing", StringComparison.OrdinalIgnoreCase))
        {
            accounts.Add(new("1210", "Work in Progress", AccountType.Asset, AccountGroup.CurrentAsset));
            accounts.Add(new("1220", "Raw Materials", AccountType.Asset, AccountGroup.CurrentAsset));
            accounts.Add(new("5200", "Factory Overhead", AccountType.Expense, AccountGroup.CostOfSales));
        }
        else if (industry.Equals("retail", StringComparison.OrdinalIgnoreCase))
        {
            accounts.Add(new("1230", "Goods Received Not Invoiced", AccountType.Liability, AccountGroup.CurrentLiability));
        }

        return accounts;
    }

    public static string Describe(string industry) => industry.ToLowerInvariant() switch
    {
        "retail" => "Retail & e-commerce",
        "manufacturing" => "Manufacturing",
        "services" => "Professional services",
        _ => "General business"
    };
}
