namespace AxiomLedger.Domain;

public class SalesInvoice : AuditableEntity
{
    public string Number { get; set; } = string.Empty;
    public InvoiceKind Kind { get; set; } = InvoiceKind.Invoice;
    public Guid PartyId { get; set; }
    public Party? Party { get; set; }
    public DateOnly IssueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public decimal ExchangeRate { get; set; } = 1m;
    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;
    public string? PurchaseOrderNumber { get; set; }
    public string? Notes { get; set; }
    public string? PaymentTerms { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public decimal AmountPaid { get; set; }
    public Guid? JournalEntryId { get; set; }
    public DateTime? IssuedAt { get; set; }
    public string? IssuedByUserId { get; set; }
    public DateTime? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public List<SalesInvoiceLine> Lines { get; set; } = new();

    public decimal BalanceDue => Money.Round(Total - AmountPaid);
    public bool IsOpen => Status is DocumentStatus.Issued or DocumentStatus.PartiallyPaid;
    public bool IsEditable => Status == DocumentStatus.Draft;

    /// <summary>Recomputes discounts, taxes and totals from the lines. All amounts are in the document currency.</summary>
    public void Recalculate()
    {
        foreach (var line in Lines)
        {
            var gross = line.Quantity * line.UnitPrice;
            line.DiscountAmount = Money.Round(gross * line.DiscountPercent / 100m);

            if (line.TaxInclusive && line.TaxRate > 0)
            {
                var net = Money.Round(gross - line.DiscountAmount);
                var tax = Money.TaxFromInclusive(net, line.TaxRate);
                line.NetAmount = Money.Round(net - tax);
                line.TaxAmount = tax;
                line.DiscountAmount = Money.Round(line.DiscountAmount);
            }
            else
            {
                line.NetAmount = Money.Round(gross - line.DiscountAmount);
                line.TaxAmount = Money.Percent(line.NetAmount, line.TaxRate);
            }
        }

        Subtotal = Money.Round(Lines.Sum(l => l.NetAmount + l.DiscountAmount));
        DiscountTotal = Money.Round(Lines.Sum(l => l.DiscountAmount));
        TaxTotal = Money.Round(Lines.Sum(l => l.TaxAmount));
        Total = Money.Round(Lines.Sum(l => l.NetAmount + l.TaxAmount));
    }

    public void RefreshStatus()
    {
        if (Status is DocumentStatus.Draft or DocumentStatus.Void) return;
        if (Kind == InvoiceKind.CreditNote)
        {
            Status = DocumentStatus.Issued;
            return;
        }
        if (AmountPaid >= Total - 0.005m && Total > 0)
            Status = DocumentStatus.Paid;
        else if (AmountPaid > 0.005m)
            Status = DocumentStatus.PartiallyPaid;
        else
            Status = DocumentStatus.Issued;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (PartyId == Guid.Empty) errors.Add("Select a customer.");
        if (Lines.Count == 0) errors.Add("An invoice needs at least one line.");
        if (DueDate < IssueDate) errors.Add("The due date cannot precede the issue date.");
        if (string.IsNullOrWhiteSpace(CurrencyCode)) errors.Add("A currency is required.");
        if (ExchangeRate <= 0) errors.Add("The exchange rate must be greater than zero.");
        foreach (var (line, index) in Lines.Select((l, i) => (l, i)))
        {
            if (string.IsNullOrWhiteSpace(line.Description)) errors.Add($"Line {index + 1}: a description is required.");
            if (line.AccountId == Guid.Empty) errors.Add($"Line {index + 1}: select a revenue account.");
            if (line.Quantity <= 0) errors.Add($"Line {index + 1}: quantity must be greater than zero.");
            if (line.UnitPrice < 0) errors.Add($"Line {index + 1}: unit price cannot be negative.");
            if (line.DiscountPercent is < 0 or > 100) errors.Add($"Line {index + 1}: discount must be between 0 and 100%.");
            if (line.TaxRate is < 0 or > 100) errors.Add($"Line {index + 1}: tax rate must be between 0 and 100%.");
        }
        return errors;
    }
}

public class SalesInvoiceLine : Entity
{
    public Guid SalesInvoiceId { get; set; }
    public SalesInvoice? SalesInvoice { get; set; }
    public int LineNumber { get; set; }
    public string Description { get; set; } = string.Empty;
    public Guid AccountId { get; set; }
    public Account? Account { get; set; }
    public decimal Quantity { get; set; } = 1m;
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal DiscountAmount { get; set; }
    public Guid? TaxCodeId { get; set; }
    public TaxCode? TaxCode { get; set; }
    public Guid? TaxAccountId { get; set; }
    public decimal TaxRate { get; set; }
    public bool TaxInclusive { get; set; }
    public decimal NetAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public Guid? DepartmentId { get; set; }
    public decimal LineTotal => Money.Round(NetAmount + TaxAmount);
}

public class Bill : AuditableEntity
{
    public string Number { get; set; } = string.Empty;
    public Guid PartyId { get; set; }
    public Party? Party { get; set; }
    public string? VendorInvoiceNumber { get; set; }
    public DateOnly IssueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public decimal ExchangeRate { get; set; } = 1m;
    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;
    public string? Notes { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public decimal AmountPaid { get; set; }
    public Guid? JournalEntryId { get; set; }
    public DateTime? IssuedAt { get; set; }
    public string? IssuedByUserId { get; set; }
    public DateTime? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public List<BillLine> Lines { get; set; } = new();

    public decimal BalanceDue => Money.Round(Total - AmountPaid);
    public bool IsOpen => Status is DocumentStatus.Issued or DocumentStatus.PartiallyPaid;
    public bool IsEditable => Status == DocumentStatus.Draft;

    public void Recalculate()
    {
        foreach (var line in Lines)
        {
            var gross = line.Quantity * line.UnitPrice;
            line.DiscountAmount = Money.Round(gross * line.DiscountPercent / 100m);
            if (line.TaxInclusive && line.TaxRate > 0)
            {
                var net = Money.Round(gross - line.DiscountAmount);
                var tax = Money.TaxFromInclusive(net, line.TaxRate);
                line.NetAmount = Money.Round(net - tax);
                line.TaxAmount = tax;
            }
            else
            {
                line.NetAmount = Money.Round(gross - line.DiscountAmount);
                line.TaxAmount = Money.Percent(line.NetAmount, line.TaxRate);
            }
        }

        Subtotal = Money.Round(Lines.Sum(l => l.NetAmount + l.DiscountAmount));
        DiscountTotal = Money.Round(Lines.Sum(l => l.DiscountAmount));
        TaxTotal = Money.Round(Lines.Sum(l => l.TaxAmount));
        Total = Money.Round(Lines.Sum(l => l.NetAmount + l.TaxAmount));
    }

    public void RefreshStatus()
    {
        if (Status is DocumentStatus.Draft or DocumentStatus.Void) return;
        if (AmountPaid >= Total - 0.005m && Total > 0) Status = DocumentStatus.Paid;
        else if (AmountPaid > 0.005m) Status = DocumentStatus.PartiallyPaid;
        else Status = DocumentStatus.Issued;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (PartyId == Guid.Empty) errors.Add("Select a vendor.");
        if (Lines.Count == 0) errors.Add("A bill needs at least one line.");
        if (DueDate < IssueDate) errors.Add("The due date cannot precede the bill date.");
        if (ExchangeRate <= 0) errors.Add("The exchange rate must be greater than zero.");
        foreach (var (line, index) in Lines.Select((l, i) => (l, i)))
        {
            if (string.IsNullOrWhiteSpace(line.Description)) errors.Add($"Line {index + 1}: a description is required.");
            if (line.AccountId == Guid.Empty) errors.Add($"Line {index + 1}: select an expense account.");
            if (line.Quantity <= 0) errors.Add($"Line {index + 1}: quantity must be greater than zero.");
            if (line.UnitPrice < 0) errors.Add($"Line {index + 1}: unit price cannot be negative.");
            if (line.TaxRate is < 0 or > 100) errors.Add($"Line {index + 1}: tax rate must be between 0 and 100%.");
        }
        return errors;
    }
}

public class BillLine : Entity
{
    public Guid BillId { get; set; }
    public Bill? Bill { get; set; }
    public int LineNumber { get; set; }
    public string Description { get; set; } = string.Empty;
    public Guid AccountId { get; set; }
    public Account? Account { get; set; }
    public decimal Quantity { get; set; } = 1m;
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal DiscountAmount { get; set; }
    public Guid? TaxCodeId { get; set; }
    public TaxCode? TaxCode { get; set; }
    public Guid? TaxAccountId { get; set; }
    public decimal TaxRate { get; set; }
    public bool TaxInclusive { get; set; }
    public decimal NetAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public Guid? DepartmentId { get; set; }
    public decimal LineTotal => Money.Round(NetAmount + TaxAmount);
}
