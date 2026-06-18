using System.Collections.Concurrent;

namespace WinDFIR.Core.Repository;

/// <summary>
/// A process-wide async mutex keyed by an arbitrary string. Used to serialize publishes that target the same
/// repository destination so two concurrent <see cref="FileSystemArtifactSink"/> calls (e.g. an HTTP intake
/// server handling overlapping uploads of the same collection, or a UI + retry) cannot race on the shared
/// "<dest>.partial" staging directory or the delete-then-rename finalize.
///
/// Scope is in-process only. Different collectionIds never collide (each is a per-collection GUID), so the
/// realistic concurrency surface is fully covered. Two *separate processes* publishing the *same* collectionId
/// to a shared filesystem are not serialized by this — see the intake deployment notes.
/// </summary>
internal static class KeyedAsyncLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private bool _released;

        public Releaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose()
        {
            if (_released)
                return;
            _released = true;
            _gate.Release();
        }
    }
}
