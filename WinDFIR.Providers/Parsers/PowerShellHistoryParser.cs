namespace WinDFIR.Providers.Parsers;

/// <summary>One command line from a PSReadLine ConsoleHost_history.txt file.</summary>
public sealed class PowerShellHistoryEntry
{
    /// <summary>1-based physical line number within the history file.</summary>
    public int LineNumber { get; init; }

    public string Command { get; init; } = "";

    /// <summary>Well-known offensive-PowerShell tokens matched in <see cref="Command"/> (heuristic triage aid, not proof).</summary>
    public IReadOnlyList<string> SuspiciousKeywords { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Parses PSReadLine command history (<c>ConsoleHost_history.txt</c>): plain UTF-8 text, one accepted
/// command per physical line. There are no per-line timestamps in the format.
/// </summary>
/// <remarks>
/// Multi-line commands are stored by PSReadLine as multiple physical lines with no escaping, so they
/// cannot be unambiguously reconstructed; each non-empty physical line is treated as one entry. This is
/// a deliberate, documented simplification (a blind guess at command boundaries would be worse).
/// </remarks>
public static class PowerShellHistoryParser
{
    // Conservative, well-known offensive-PowerShell substrings (case-insensitive).
    private static readonly string[] SuspiciousTokens =
    {
        "-enc", "-encodedcommand", "frombase64string", "downloadstring", "downloadfile",
        "iex", "invoke-expression", "invoke-webrequest", "net.webclient", "-nop", "-noprofile",
        "-w hidden", "-windowstyle hidden", "hidden", "bypass", "-noni", "-noninteractive",
        "certutil", "bitsadmin", "mimikatz", "reflection.assembly", "frombase64", "start-process",
        "set-mppreference", "add-mppreference", "[char]", "downloadfileasync", "webclient"
    };

    public static IReadOnlyList<PowerShellHistoryEntry> ParseHistory(string? content)
    {
        var entries = new List<PowerShellHistoryEntry>();
        if (string.IsNullOrEmpty(content))
            return entries;

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var command = lines[i].Trim();
            if (command.Length == 0)
                continue;

            entries.Add(new PowerShellHistoryEntry
            {
                LineNumber = i + 1,
                Command = command,
                SuspiciousKeywords = DetectSuspiciousKeywords(command)
            });
        }

        return entries;
    }

    public static IReadOnlyList<string> DetectSuspiciousKeywords(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return Array.Empty<string>();

        var matches = new List<string>();
        foreach (var token in SuspiciousTokens)
        {
            if (command.Contains(token, StringComparison.OrdinalIgnoreCase) && !matches.Contains(token))
                matches.Add(token);
        }
        return matches;
    }
}
