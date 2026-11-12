using AxiomLedger.Domain;

namespace AxiomLedger.Application.Models;

public sealed record TrialBalanceRow(Guid AccountId, string Code, string Name, AccountType Type,
    AccountGroup Group, decimal OpeningBalance, decimal Debit, decimal Credit, decimal ClosingBalance);

public sealed record TrialBalanceReport(DateOnly From, DateOnly To, IReadOnlyList<TrialBalanceRow> Rows,
    decimal OpeningTotal, decimal TotalDebit, decimal TotalCredit, decimal ClosingTotal)
{
    public bool IsBalanced => Money.IsBalanced(TotalDebit, TotalCredit) && Money.IsZero(ClosingTotal);
}

public sealed record StatementRow(string? Code, string Label, decimal Amount, int Depth = 0,
    bool IsTotal = false, Guid? AccountId = null);

public sealed record StatementSection(string Title, IReadOnlyList<StatementRow> Rows, decimal Total,
    string? Key = null);

public sealed record ProfitAndLossReport(DateOnly From, DateOnly To,
    IReadOnlyList<StatementSection> Sections, decimal Revenue, decimal CostOfSales, decimal GrossProfit,
    decimal OperatingExpenses, decimal OperatingProfit, decimal OtherIncome, decimal OtherExpense,
    decimal NetProfit, ProfitAndLossReport? Comparative = null);

public sealed record BalanceSheetReport(DateOnly AsOf, IReadOnlyList<StatementSection> Sections,
    decimal TotalAssets, decimal TotalLiabilities, decimal TotalEquity, decimal CurrentEarnings,
    decimal Difference, bool IsBalanced);

public sealed record CashFlowReport(DateOnly From, DateOnly To, IReadOnlyList<StatementSection> Sections,
    decimal NetCashFlow, decimal OpeningCash, decimal ClosingCash);

public sealed record GeneralLedgerRow(Guid JournalEntryId, string EntryNumber, DateOnly Date, string Memo,
    string? Reference, string? LineDescription, decimal Debit, decimal Credit, decimal Balance,
    Guid? PartyId, string? PartyName, JournalSource Source);

public sealed record GeneralLedgerReport(Guid AccountId, string AccountCode, string AccountName,
    DateOnly From, DateOnly To, decimal OpeningBalance, IReadOnlyList<GeneralLedgerRow> Rows,
    decimal TotalDebit, decimal TotalCredit, decimal ClosingBalance);

public sealed record AgingRow(Guid PartyId, string Code, string Name, string CurrencyCode, decimal Current,
    decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90, decimal Total,
    decimal UnappliedCredits)
{
    public decimal NetExposure => Money.Round(Total - UnappliedCredits);
}

public sealed record AgingReport(DateOnly AsOf, IReadOnlyList<AgingRow> Rows, decimal Current,
    decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90, decimal Total);

public sealed record PartyStatementRow(DateOnly Date, string DocumentType, string Number, string? Reference,
    decimal Debit, decimal Credit, decimal Balance);

public sealed record PartyStatement(Guid PartyId, string PartyName, DateOnly From, DateOnly To,
    decimal OpeningBalance, IReadOnlyList<PartyStatementRow> Rows, decimal ClosingBalance,
    decimal TotalInvoiced, decimal TotalPaid);

public sealed record TaxDetailRow(String Period, string TaxCode, decimal Rate, decimal TaxableAmount,
    decimal TaxAmount);

public sealed record TaxReport(DateOnly From, DateOnly To, IReadOnlyList<TaxSummaryRow> Summary,
    IReadOnlyList<TaxDetailRow> Detail, decimal SalesTaxCollected, decimal PurchaseTaxPaid,
    decimal NetLiability);

public sealed record AccountBalanceRow(Guid AccountId, string Code, string Name, AccountType Type,
    AccountGroup Group, decimal Balance);

/// <summary>Static, dependency-free CSV writer used by the report screens and CLI.</summary>
public static class CsvWriter
{
    public static string Write(IEnumerable<string> headers, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Escape)));
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", row.Select(cell => Escape(Format(cell)))));
        return sb.ToString();
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        decimal d => d.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
