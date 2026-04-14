namespace WinDFIR.Core.Mft;

/// <summary>
/// Represents a parsed MFT (Master File Table) record with minimal fields for DFIR use.
/// </summary>
public record MftEntry
{
    /// <summary>Zero-based MFT record index (position in stream / record size).</summary>
    public long RecordIndex { get; init; }

    /// <summary>Source label for the loaded MFT (e.g. Volume C:, exported file path).</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>File name from the $FILE_NAME attribute (e.g. short or long name).</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Full path when available (built from parent chain); otherwise same as FileName.</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>Creation time (UTC) from $STANDARD_INFORMATION.</summary>
    public DateTime? CreatedUtc { get; init; }

    /// <summary>Last modification time (UTC) from $STANDARD_INFORMATION.</summary>
    public DateTime? ModifiedUtc { get; init; }

    /// <summary>Parent directory MFT record index from $FILE_NAME (48-bit file reference).</summary>
    public long? ParentRecordIndex { get; init; }

    /// <summary>Creation time (UTC) from $FILE_NAME attribute.</summary>
    public DateTime? CreatedUtcFn { get; init; }

    /// <summary>Last modification time (UTC) from $FILE_NAME attribute.</summary>
    public DateTime? ModifiedUtcFn { get; init; }

    /// <summary>True when $STANDARD_INFORMATION and $FILE_NAME timestamps differ significantly (possible time-stomping).</summary>
    public bool TimeStompSuspect { get; init; }

    /// <summary>True if the FILE record flags indicate the record is in use.</summary>
    public bool IsInUse { get; init; }

    /// <summary>True if the entry is a directory (from $FILE_NAME file flags).</summary>
    public bool IsDirectory { get; init; }
}
