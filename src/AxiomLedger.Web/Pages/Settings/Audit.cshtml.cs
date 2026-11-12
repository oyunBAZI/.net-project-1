using AxiomLedger.Application;
using AxiomLedger.Domain;
using AxiomLedger.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Web.Pages.Settings;

public sealed class AuditModel : AppPageModel
{
    private const int PageSize = 50;
    private readonly IAccountingDbContext _db;

    public AuditModel(IAccountingDbContext db) => _db = db;

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public int Page { get; set; } = 1;

    public IReadOnlyList<AuditLog> Events { get; private set; } = Array.Empty<AuditLog>();
    public int Total { get; private set; }
    public int PageCount => (int)Math.Ceiling(Total / (double)PageSize);

    public async Task<IActionResult> OnGetAsync()
    {
        if (CompanyId is null) return RedirectToPage("/SwitchCompany");
        if (!Can(Permission.ViewAudit)) return ForbiddenResult();

        var query = _db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == CompanyId);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            query = query.Where(a => a.Action.Contains(term) || a.EntityType.Contains(term) ||
                                     (a.UserEmail != null && a.UserEmail.Contains(term)) ||
                                     (a.Details != null && a.Details.Contains(term)));
        }
        if (From.HasValue)
            query = query.Where(a => a.OccurredAt >= From.Value.ToDateTime(TimeOnly.MinValue));
        if (To.HasValue)
            query = query.Where(a => a.OccurredAt < To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue));

        Total = await query.CountAsync();
        Events = await query.OrderByDescending(a => a.OccurredAt)
            .Skip((Math.Max(1, Page) - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();

        return Page();
    }
}
