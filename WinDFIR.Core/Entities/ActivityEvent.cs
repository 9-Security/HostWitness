namespace WinDFIR.Core.Entities;

/// <summary>
/// Unified activity event contract for correlation.
/// All providers normalize their data into ActivityEvent instances.
/// Per specification: ActivityEvent Schema with category, action, subject, object, summary, fields, evidence array, confidence.
/// </summary>
public record ActivityEvent
{
    // Required fields per specification
    public required DateTime Timestamp { get; init; }
    public required string Category { get; init; } // Process | File | Network | Registry | Browser | Logon
    public required string Action { get; init; } // Start | Stop | Open | Write | Connect | Query
    public required List<EvidenceRef> Evidence { get; init; } = new();
    
    // Subject: UserKey or ProcessKey (per specification)
    public UserKey? SubjectUser { get; init; }
    public ProcessKey? SubjectProcess { get; init; }
    
    // Object: FileKey | RegKey | FlowKey | URL (per specification)
    public FileKey? ObjectFile { get; init; }
    public RegistryKey? ObjectRegistry { get; init; }
    public NetworkFlowKey? ObjectNetworkFlow { get; init; }
    public string? ObjectUrl { get; init; }
    
    // Additional fields
    public string? Summary { get; init; } // Human-readable description
    public Dictionary<string, object> Fields { get; init; } = new(); // Source-specific fields (renamed from Properties)
    public string Confidence { get; init; } = "Medium"; // High | Medium | Low
    
    // Legacy support: EventType for backward compatibility
    [Obsolete("Use Category and Action instead")]
    public string? EventType { get; init; }
    
    // Legacy support: Single EvidenceRef for backward compatibility
    [Obsolete("Use Evidence list instead")]
    public EvidenceRef? LegacyEvidence { get; init; }
    
    // Legacy support: Direct entity references (for backward compatibility)
    [Obsolete("Use SubjectProcess instead")]
    public ProcessKey? ProcessKey { get; init; }
    
    [Obsolete("Use ObjectNetworkFlow instead")]
    public NetworkFlowKey? NetworkKey { get; init; }

    public override string ToString() => $"{Category}.{Action} @ {Timestamp:O} [{Evidence.Count} evidence]";
}
