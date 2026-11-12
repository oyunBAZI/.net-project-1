namespace AxiomLedger.Domain;

/// <summary>Classification of a ledger account. Drives normal balance and reporting placement.</summary>
public enum AccountType
{
    Asset = 1,
    Liability = 2,
    Equity = 3,
    Revenue = 4,
    Expense = 5
}

public enum NormalBalance
{
    Debit = 1,
    Credit = 2
}

/// <summary>How a cash movement is classified on the statement of cash flows.</summary>
public enum CashFlowCategory
{
    None = 0,
    Operating = 1,
    Investing = 2,
    Financing = 3
}

/// <summary>Statement classification used to build a professional balance sheet and P&amp;L.</summary>
public enum AccountGroup
{
    None = 0,
    CurrentAsset = 1,
    FixedAsset = 2,
    OtherAsset = 3,
    CurrentLiability = 4,
    NonCurrentLiability = 5,
    Equity = 6,
    Revenue = 7,
    CostOfSales = 8,
    OperatingExpense = 9,
    OtherIncome = 10,
    OtherExpense = 11
}

public static class AccountGroups
{
    public static AccountGroup DefaultFor(AccountType type) => type switch
    {
        AccountType.Asset => AccountGroup.CurrentAsset,
        AccountType.Liability => AccountGroup.CurrentLiability,
        AccountType.Equity => AccountGroup.Equity,
        AccountType.Revenue => AccountGroup.Revenue,
        AccountType.Expense => AccountGroup.OperatingExpense,
        _ => AccountGroup.None
    };

    public static CashFlowCategory CashFlowDefault(AccountGroup group) => group switch
    {
        AccountGroup.FixedAsset or AccountGroup.OtherAsset => CashFlowCategory.Investing,
        AccountGroup.NonCurrentLiability or AccountGroup.Equity => CashFlowCategory.Financing,
        _ => CashFlowCategory.Operating
    };
}

public enum PeriodStatus
{
    Open = 1,
    SoftClosed = 2,
    Closed = 3
}

public enum FiscalYearStatus
{
    Open = 1,
    Closed = 2
}

public enum TaxKind
{
    Sales = 1,
    Purchase = 2,
    Both = 3
}

public enum PartyType
{
    Customer = 1,
    Vendor = 2,
    Both = 3
}

public enum JournalSource
{
    Manual = 1,
    OpeningBalance = 2,
    SalesInvoice = 3,
    SalesCreditNote = 4,
    PurchaseBill = 5,
    PurchaseCreditNote = 6,
    CustomerReceipt = 7,
    VendorPayment = 8,
    BankImport = 9,
    Depreciation = 10,
    Payroll = 11,
    YearEndClose = 12,
    FxRevaluation = 13
}

public enum JournalStatus
{
    Draft = 1,
    PendingApproval = 2,
    Posted = 3,
    Reversed = 4,
    Void = 5
}

public enum InvoiceKind
{
    Invoice = 1,
    CreditNote = 2
}

public enum DocumentStatus
{
    Draft = 1,
    Issued = 2,
    PartiallyPaid = 3,
    Paid = 4,
    Void = 5
}

public enum PaymentDirection
{
    Receipt = 1,
    Disbursement = 2
}

public enum PaymentMethod
{
    BankTransfer = 1,
    Cash = 2,
    Card = 3,
    Cheque = 4,
    DirectDebit = 5,
    Other = 6
}

public enum BankLineStatus
{
    Unmatched = 1,
    Matched = 2,
    Excluded = 3
}

public enum ReconciliationStatus
{
    InProgress = 1,
    Completed = 2
}

public enum UserRole
{
    Viewer = 1,
    Bookkeeper = 2,
    Accountant = 3,
    Approver = 4,
    Admin = 5,
    Owner = 6
}

[Flags]
public enum Permission
{
    None = 0,
    ViewAccounts = 1 << 0,
    ManageAccounts = 1 << 1,
    ViewJournal = 1 << 2,
    CreateJournal = 1 << 3,
    PostJournal = 1 << 4,
    ApproveJournal = 1 << 5,
    ManageParties = 1 << 6,
    ManageSales = 1 << 7,
    ManagePurchases = 1 << 8,
    ManagePayments = 1 << 9,
    ManageBanking = 1 << 10,
    ViewReports = 1 << 11,
    ManageSettings = 1 << 12,
    ManageUsers = 1 << 13,
    ClosePeriods = 1 << 14,
    ViewAudit = 1 << 15,
    ManageCompanies = 1 << 16,

    ReadOnly = ViewAccounts | ViewJournal | ViewReports,
    All = ~0
}

/// <summary>Role based access control. Roles are additive and evaluated per company membership.</summary>
public static class RolePermissions
{
    public static Permission For(UserRole role) => role switch
    {
        UserRole.Viewer => Permission.ReadOnly,
        UserRole.Bookkeeper => Permission.ReadOnly
            | Permission.CreateJournal | Permission.ManageParties
            | Permission.ManageSales | Permission.ManagePurchases | Permission.ManagePayments,
        UserRole.Accountant => Permission.ReadOnly
            | Permission.CreateJournal | Permission.PostJournal | Permission.ManageParties
            | Permission.ManageSales | Permission.ManagePurchases | Permission.ManagePayments
            | Permission.ManageBanking | Permission.ManageAccounts | Permission.ViewAudit,
        UserRole.Approver => Permission.ReadOnly
            | Permission.ViewJournal | Permission.ApproveJournal | Permission.PostJournal
            | Permission.ViewAudit,
        UserRole.Admin => Permission.All
            & ~(Permission.ManageCompanies | Permission.ClosePeriods),
        UserRole.Owner => Permission.All,
        _ => Permission.None
    };

    public static bool Allows(UserRole role, Permission permission) =>
        (For(role) & permission) == permission;

    public static string Describe(UserRole role) => role switch
    {
        UserRole.Viewer => "Read-only access to the ledger and reports.",
        UserRole.Bookkeeper => "Captures invoices, bills, payments and draft journals.",
        UserRole.Accountant => "Full day-to-day accounting including posting and banking.",
        UserRole.Approver => "Reviews and approves journals; cannot edit master data.",
        UserRole.Admin => "Administers users, settings and accounting data.",
        UserRole.Owner => "Unrestricted access, including closing books and companies.",
        _ => string.Empty
    };
}
