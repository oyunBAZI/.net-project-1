using AxiomLedger.Domain;
using Microsoft.EntityFrameworkCore;

namespace AxiomLedger.Application.Services;

/// <summary>Allocates gap-free document numbers per company. Callers hold the company lock around save.</summary>
public sealed class NumberSequenceService
{
    private readonly IAccountingDbContext _db;

    public NumberSequenceService(IAccountingDbContext db) => _db = db;

    public async Task<string> NextAsync(Guid companyId, string key, string prefix, CancellationToken ct = default)
    {
        var sequence = await _db.DocumentSequences
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Key == key, ct);

        if (sequence is null)
        {
            sequence = new DocumentSequence
            {
                CompanyId = companyId,
                Key = key,
                Prefix = string.IsNullOrWhiteSpace(prefix) ? key.ToUpperInvariant() : prefix,
                NextNumber = 1
            };
            _db.DocumentSequences.Add(sequence);
        }

        var number = sequence.Format(sequence.NextNumber);
        sequence.NextNumber++;
        return number;
    }

    public async Task<string> PeekNextAsync(Guid companyId, string key, string prefix, CancellationToken ct = default)
    {
        var sequence = await _db.DocumentSequences
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Key == key, ct);
        if (sequence is null)
            return $"{prefix}-{1.ToString().PadLeft(5, '0')}";
        return sequence.Format(sequence.NextNumber);
    }
}
