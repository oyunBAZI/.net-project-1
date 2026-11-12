namespace AxiomLedger.Domain;

/// <summary>Base class for expected, business-rule failures. These are surfaced to users, never mapped to 500s.</summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}

/// <summary>Carries one or more field-level validation problems from the accounting engine.</summary>
public sealed class DomainValidationException : DomainException
{
    public DomainValidationException(string message) : base(message)
    {
        Errors = new[] { message };
    }

    public DomainValidationException(IEnumerable<string> errors)
        : base(string.Join(" ", errors))
    {
        Errors = errors.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct().ToArray();
    }

    public IReadOnlyList<string> Errors { get; }
}

public sealed class NotFoundException : DomainException
{
    public NotFoundException(string message) : base(message) { }
}

public sealed class PermissionDeniedException : DomainException
{
    public PermissionDeniedException(string message = "You do not have permission to perform this action.")
        : base(message) { }
}
