namespace WinDFIR.Core.Repository;

/// <summary>
/// A process-wide async mutex keyed by an arbitrary string. Used to serialize publishes that target the same
/// repository destination so two concurrent <see cref="FileSystemArtifactSink"/> calls (e.g. an HTTP intake
/// server handling overlapping uploads of the same collection, or a UI + retry) cannot race on the shared
/// "<dest>.partial" staging directory or the delete-then-rename finalize.
///
/// Entries are reference-counted and evicted (the underlying semaphore disposed) when the last holder/waiter
/// for a key releases, so a long-lived intake server processing many per-collection-GUID keys does not leak a
/// SemaphoreSlim per collection forever.
///
/// Scope is in-process only. Two *separate processes* publishing the *same* collectionId to a shared
/// filesystem are not serialized by this — see the intake deployment notes.
/// </summary>
internal static class KeyedAsyncLock
{
    private sealed class Gate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }

    private static readonly Dictionary<string, Gate> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        Gate gate;
        lock (Locks)
        {
            if (!Locks.TryGetValue(key, out gate!))
            {
                gate = new Gate();
                Locks[key] = gate;
            }
            gate.RefCount++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            // Cancelled before acquiring: drop our reference (and evict the key if we were the last interest).
            Release(key, gate, acquired: false);
            throw;
        }

        return new Releaser(key, gate);
    }

    private static void Release(string key, Gate gate, bool acquired)
    {
        if (acquired)
            gate.Semaphore.Release();

        lock (Locks)
        {
            if (--gate.RefCount == 0)
            {
                Locks.Remove(key);
                gate.Semaphore.Dispose();
            }
        }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly string _key;
        private readonly Gate _gate;
        private bool _released;

        public Releaser(string key, Gate gate)
        {
            _key = key;
            _gate = gate;
        }

        public void Dispose()
        {
            if (_released)
                return;
            _released = true;
            Release(_key, _gate, acquired: true);
        }
    }
}
