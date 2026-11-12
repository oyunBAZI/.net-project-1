using AxiomLedger.Application.Models;
using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

public sealed class PartyService
{
    private readonly IAccountingDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public PartyService(IAccountingDbContext db, ICurrentUser user, IClock clock, AuditService audit)
    {
        _db = db;
        _user = user;
        _clock = clock;
        _audit = audit;
    }

    public async Task<IReadOnlyList<PartyListItem>> ListAsync(Guid companyId, string? search = null,
        PartyType? type = null, bool includeInactive = false, CancellationToken ct = default)
    {
        var query = _db.Parties.AsNoTracking().Where(p => p.CompanyId == companyId);
        if (!includeInactive) query = query.Where(p => p.IsActive);
        if (type == PartyType.Customer)
            query = query.Where(p => p.Type == PartyType.Customer || p.Type == PartyType.Both);
        if (type == PartyType.Vendor)
            query = query.Where(p => p.Type == PartyType.Vendor || p.Type == PartyType.Both);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(p => p.Name.Contains(term) || p.Code.Contains(term) ||
                                     (p.Email != null && p.Email.Contains(term)));
        }

        var parties = await query.OrderBy(p => p.Name).ToListAsync(ct);

        var receivable = await _db.SalesInvoices.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.Kind == InvoiceKind.Invoice &&
                        (i.Status == DocumentStatus.Issued || i.Status == DocumentStatus.PartiallyPaid))
            .GroupBy(i => i.PartyId)
            .Select(g => new
            {
                PartyId = g.Key,
                Balance = g.Sum(i => (double)((i.Total - i.AmountPaid) * i.ExchangeRate))
            })
            .ToDictionaryAsync(x => x.PartyId, x => (decimal)x.Balance, ct);

        var payable = await _db.Bills.AsNoTracking()
            .Where(b => b.CompanyId == companyId &&
                        (b.Status == DocumentStatus.Issued || b.Status == DocumentStatus.PartiallyPaid))
            .GroupBy(b => b.PartyId)
            .Select(g => new
            {
                PartyId = g.Key,
                Balance = g.Sum(b => (double)((b.Total - b.AmountPaid) * b.ExchangeRate))
            })
            .ToDictionaryAsync(x => x.PartyId, x => (decimal)x.Balance, ct);

        return parties.Select(p => new PartyListItem(p.Id, p.Code, p.Name, p.Type, p.Email, p.CurrencyCode,
            p.IsActive,
            Money.Round(receivable.TryGetValue(p.Id, out var ar) ? ar : 0m),
            Money.Round(payable.TryGetValue(p.Id, out var ap) ? ap : 0m))).ToList();
    }

    public async Task<Party?> GetAsync(Guid companyId, Guid id, CancellationToken ct = default) =>
        await _db.Parties.FirstOrDefaultAsync(p => p.Id == id && p.CompanyId == companyId, ct);

    public async Task<Party> CreateAsync(Guid companyId, PartyRequest request, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageParties);
        Validate(request);

        using var _ = await CompanyLock.AcquireAsync(companyId, ct);
        var party = new Party
        {
            CompanyId = companyId,
            CreatedAt = _clock.UtcNow,
            CreatedByUserId = _user.UserId
        };
        await ApplyAsync(party, request, ct);
        _db.Parties.Add(party);
        _audit.Record(companyId, "PartyCreated", nameof(Party), party.Id.ToString(), party.Name);
        await _db.SaveChangesAsync(ct);
        return party;
    }

    public async Task<Party> UpdateAsync(Guid companyId, Guid id, PartyRequest request,
        CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageParties);
        Validate(request);

        var party = await GetAsync(companyId, id, ct) ?? throw new NotFoundException("Party not found.");
        await ApplyAsync(party, request, ct);
        party.UpdatedAt = _clock.UtcNow;
        party.UpdatedByUserId = _user.UserId;

        _audit.Record(companyId, "PartyUpdated", nameof(Party), party.Id.ToString(), party.Name);
        await _db.SaveChangesAsync(ct);
        return party;
    }

    public async Task SetActiveAsync(Guid companyId, Guid id, bool active, CancellationToken ct = default)
    {
        PermissionGuard.Demand(_user, Permission.ManageParties);
        var party = await GetAsync(companyId, id, ct) ?? throw new NotFoundException("Party not found.");
        party.IsActive = active;
        _audit.Record(companyId, active ? "PartyActivated" : "PartyDeactivated", nameof(Party),
            party.Id.ToString(), party.Name);
        await _db.SaveChangesAsync(ct);
    }

    private async Task ApplyAsync(Party party, PartyRequest request, CancellationToken ct)
    {
        party.Type = request.Type;
        party.Name = request.Name.Trim();
        party.Email = request.Email;
        party.Phone = request.Phone;
        party.ContactName = request.ContactName;
        party.TaxId = request.TaxId;
        party.AddressLine1 = request.AddressLine1;
        party.AddressLine2 = request.AddressLine2;
        party.City = request.City;
        party.Region = request.Region;
        party.PostalCode = request.PostalCode;
        party.CountryCode = request.CountryCode;
        party.CurrencyCode = string.IsNullOrWhiteSpace(request.CurrencyCode) ? "USD" : request.CurrencyCode;
        party.PaymentTermsDays = request.PaymentTermsDays;
        party.CreditLimit = request.CreditLimit;
        party.IsActive = request.IsActive;
        party.Notes = request.Notes;

        if (!string.IsNullOrWhiteSpace(request.Code))
        {
            party.Code = request.Code.Trim().ToUpperInvariant();
            var duplicate = await _db.Parties.AnyAsync(p =>
                p.CompanyId == party.CompanyId && p.Code == party.Code && p.Id != party.Id, ct);
            if (duplicate)
                throw new DomainValidationException($"Another party already uses code {party.Code}.");
        }
        else if (string.IsNullOrWhiteSpace(party.Code))
        {
            var prefix = request.Type == PartyType.Vendor ? "VEND" : "CUST";
            var count = await _db.Parties.CountAsync(p => p.CompanyId == party.CompanyId, ct);
            var code = $"{prefix}-{(count + 1):0000}";
            while (await _db.Parties.AnyAsync(p => p.CompanyId == party.CompanyId && p.Code == code, ct))
                code = $"{prefix}-{(++count + 1):0000}";
            party.Code = code;
        }
    }

    private static void Validate(PartyRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Name)) errors.Add("A name is required.");
        if (!string.IsNullOrWhiteSpace(request.Email) && !request.Email.Contains('@'))
            errors.Add("Enter a valid email address.");
        if (request.PaymentTermsDays is < 0 or > 365) errors.Add("Payment terms must be between 0 and 365 days.");
        if (request.CreditLimit is < 0) errors.Add("The credit limit cannot be negative.");
        if (string.IsNullOrWhiteSpace(request.CurrencyCode) || request.CurrencyCode.Trim().Length != 3)
            errors.Add("The currency must be a three-letter ISO code.");
        if (errors.Count > 0) throw new DomainValidationException(errors);
    }
}
