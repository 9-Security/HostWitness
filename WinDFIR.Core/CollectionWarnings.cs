using System.Collections.Concurrent;

namespace WinDFIR.Core;

/// <summary>
/// Thread-safe collection of file-lock or access warnings from providers.
/// UI can snapshot and clear to show "Warnings: ..." and avoid duplicate spam.
/// Used to fully address LIMITATIONS §1 (file locking visibility).
/// </summary>
public static class CollectionWarnings
{
    private static readonly ConcurrentQueue<string> _messages = new();

    /// <summary>Add a warning (e.g. "BrowserHistory: file locked").</summary>
    public static void Add(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _messages.Enqueue(message.Trim());
    }

    /// <summary>Take a snapshot of current messages and clear the queue. Returns empty if none.</summary>
    public static string[] SnapshotAndClear()
    {
        var list = new List<string>();
        while (_messages.TryDequeue(out var msg))
            list.Add(msg);
        return list.ToArray();
    }
}
