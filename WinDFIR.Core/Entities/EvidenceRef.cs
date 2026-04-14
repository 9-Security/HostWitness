namespace WinDFIR.Core.Entities;

/// <summary>
/// Reference to source evidence: identifies where an event or entity was derived from.
/// Enables traceability from normalized data back to raw artifacts.
/// Per specification: evidence array with source, reference, and optional hash.
/// </summary>
public readonly record struct EvidenceRef
{
    public string Source { get; init; }
    public string Reference { get; init; }
    public string? Hash { get; init; }
    public DateTime? CollectedAt { get; init; }

    public EvidenceRef(string source, string reference, string? hash = null, DateTime? collectedAt = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        Hash = hash;
        CollectedAt = collectedAt ?? DateTime.UtcNow;
    }

    public override string ToString() => Hash != null 
        ? $"{Source}:{Reference} (SHA256:{Hash[..16]}...)" 
        : $"{Source}:{Reference}";
}
