namespace AxiomLedger.Domain;

/// <summary>
/// Monetary rounding helpers. Accounting amounts are rounded half-away-from-zero to the currency's
/// smallest unit; every ledger posting is made in the company's base currency.
/// </summary>
public static class Money
{
    public const int BaseDecimals = 2;
    public const decimal BalanceTolerance = 0.004m;

    public static decimal Round(decimal value, int decimals = BaseDecimals) =>
        Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    public static bool IsZero(decimal value, int decimals = BaseDecimals) =>
        Math.Abs(value) < 0.005m;

    public static bool IsBalanced(decimal debit, decimal credit, decimal tolerance = BalanceTolerance) =>
        Math.Abs(debit - credit) <= tolerance;

    public static decimal Percent(decimal amount, decimal ratePercent) =>
        Round(amount * ratePercent / 100m);

    /// <summary>Extracts the tax component from a tax-inclusive amount.</summary>
    public static decimal TaxFromInclusive(decimal gross, decimal ratePercent)
    {
        if (ratePercent == 0m) return 0m;
        var net = Round(gross / (1m + ratePercent / 100m));
        return Round(gross - net);
    }

    /// <summary>Splits an amount into instalments without losing a cent to rounding.</summary>
    public static IReadOnlyList<decimal> Allocate(decimal total, int parts)
    {
        if (parts <= 0) throw new ArgumentOutOfRangeException(nameof(parts));
        var rounded = Round(total);
        var per = Round(rounded / parts);
        var result = new List<decimal>(parts);
        var allocated = 0m;
        for (var i = 0; i < parts; i++)
        {
            var value = i == parts - 1 ? rounded - allocated : per;
            result.Add(value);
            allocated += value;
        }
        return result;
    }

    public static string Format(decimal value, string currencyCode = "USD", int decimals = BaseDecimals)
    {
        var symbol = CurrencySymbol(currencyCode);
        var abs = Math.Abs(value).ToString("N" + decimals, System.Globalization.CultureInfo.InvariantCulture);
        return value < 0 ? $"-{symbol}{abs}" : $"{symbol}{abs}";
    }

    public static string CurrencySymbol(string? code) => (code ?? "USD").ToUpperInvariant() switch
    {
        "USD" => "$",
        "EUR" => "\u20ac",
        "GBP" => "\u00a3",
        "JPY" => "\u00a5",
        "INR" => "\u20b9",
        "AUD" => "A$",
        "CAD" => "C$",
        "CHF" => "CHF ",
        "CNY" => "\u00a5",
        "TRY" => "\u20ba",
        "BRL" => "R$",
        "ZAR" => "R",
        "NGN" => "\u20a6",
        "AED" => "AED ",
        "SAR" => "SAR ",
        "MXN" => "MX$",
        "SGD" => "S$",
        "NZD" => "NZ$",
        _ => (code ?? "USD").ToUpperInvariant() + " "
    };
}
