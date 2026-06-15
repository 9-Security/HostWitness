namespace WinDFIR.Core.Repository;

public enum BundlePublishStatus
{
    /// <summary>The bundle was copied into the repository and verified at the destination.</summary>
    Published,

    /// <summary>A verified bundle with the same collectionId was already present; nothing was copied.</summary>
    AlreadyPresent,

    /// <summary>Publishing did not complete (e.g. source failed integrity, or the copy could not be verified).</summary>
    Failed
}

/// <summary>Outcome of an <see cref="IArtifactSink.PublishBundleAsync"/> call.</summary>
public sealed class BundlePublishResult
{
    public BundlePublishStatus Status { get; init; }

    /// <summary>Final repository path the bundle lives at (set when Published or AlreadyPresent).</summary>
    public string? DestinationPath { get; init; }

    public string? CollectionId { get; init; }

    public string? Hostname { get; init; }

    public int FilesCopied { get; init; }

    public int FilesSkipped { get; init; }

    public long BytesCopied { get; init; }

    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();

    public bool IsSuccess => Status != BundlePublishStatus.Failed;

    public static BundlePublishResult Fail(string issue, string? collectionId = null, string? hostname = null) =>
        new()
        {
            Status = BundlePublishStatus.Failed,
            CollectionId = collectionId,
            Hostname = hostname,
            Issues = new[] { issue }
        };

    public static BundlePublishResult Fail(IReadOnlyList<string> issues, string? collectionId = null, string? hostname = null) =>
        new()
        {
            Status = BundlePublishStatus.Failed,
            CollectionId = collectionId,
            Hostname = hostname,
            Issues = issues
        };
}

/// <summary>Progress callback payload during a publish.</summary>
public sealed class BundlePublishProgress
{
    public required string CurrentFile { get; init; }

    public int FilesProcessed { get; init; }

    public int TotalFiles { get; init; }
}
