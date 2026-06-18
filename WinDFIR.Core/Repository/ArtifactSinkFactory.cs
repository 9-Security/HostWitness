using System.Net.Http;

namespace WinDFIR.Core.Repository;

/// <summary>
/// The single rule for turning a repository-target string into an <see cref="IArtifactSink"/>: an http(s)://
/// URL routes to <see cref="HttpArtifactSink"/> (over a returned, caller-owned <see cref="HttpClient"/>); any
/// other value is a filesystem path (local dir, UNC share, or mounted bucket) handled by
/// <see cref="FileSystemArtifactSink"/>. Centralized so the Agent CLI and the desktop UI cannot drift on what a
/// given target means, and so transport-detection is testable outside a console Main / WPF event handler.
/// </summary>
public static class ArtifactSinkFactory
{
    /// <summary>True if the target is an http(s):// intake URL rather than a filesystem path.</summary>
    public static bool IsHttpTarget(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a sink for <paramref name="target"/>. When the target is an http(s):// intake, a new
    /// <see cref="HttpClient"/> is created and returned via <paramref name="httpClient"/> — the caller owns its
    /// lifetime and must dispose it. For a filesystem target, <paramref name="httpClient"/> is null.
    /// </summary>
    public static IArtifactSink Create(string target, string? token, out HttpClient? httpClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        if (IsHttpTarget(target))
        {
            httpClient = new HttpClient();
            return new HttpArtifactSink(httpClient, target, token);
        }

        httpClient = null;
        return new FileSystemArtifactSink(target);
    }
}
