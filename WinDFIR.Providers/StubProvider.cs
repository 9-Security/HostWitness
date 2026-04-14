using System.Diagnostics;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Normalization;
using ActivityEvent = WinDFIR.Core.Entities.ActivityEvent;

namespace WinDFIR.Providers;

/// <summary>
/// Stub provider for M0: generates mock ActivityEvents for testing the pipeline.
/// </summary>
public class StubProvider : IProvider
{
    public string Name => "StubProvider";

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _collectionTask;

    public event EventHandler<ActivityEvent>? EventProduced;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_collectionTask != null && !_collectionTask.IsCompleted)
            return Task.CompletedTask;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _collectionTask = Task.Run(() => CollectMockEvents(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
        
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cancellationTokenSource?.Cancel();
        
        if (_collectionTask != null)
        {
            try
            {
                await _collectionTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
            }
        }
    }

    private async Task CollectMockEvents(CancellationToken cancellationToken)
    {
        var random = new Random();
        var bootId = (ulong)Environment.TickCount64;
        var processId = (uint)Process.GetCurrentProcess().Id;
        var createTime = DateTime.UtcNow.AddSeconds(-random.Next(0, 3600));

        while (!cancellationToken.IsCancellationRequested)
        {
            var processKey = KeyGenerator.GenerateProcessKey(bootId, processId, createTime);
            
            // Per specification: ActivityEvent with Category, Action, Subject, Object, Evidence array
            var mockEvent = new ActivityEvent
            {
                Category = "Process",
                Action = "Start",
                Timestamp = DateTime.UtcNow,
                Evidence = new List<EvidenceRef>
                {
                    new EvidenceRef("StubProvider", $"mock-{Guid.NewGuid()}")
                },
                SubjectProcess = processKey,
                Summary = "Mock process creation event for testing",
                Fields = new Dictionary<string, object>
                {
                    ["ProcessName"] = "MockProcess.exe",
                    ["CommandLine"] = "mock.exe --test",
                    ["ParentPid"] = 1
                },
                Confidence = "High"
            };

            EventProduced?.Invoke(this, mockEvent);

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }
}
