using WinDFIR.Core.Entities;
using WinDFIR.Providers;
using Xunit;

namespace WinDFIR.Tests;

public class ProviderLifecycleHelperTests
{
    [Fact]
    public async Task StartProvidersAsync_RollbackStopsStartedProviders_WhenCancellationFiresBeforeLaterStart()
    {
        var cts = new CancellationTokenSource();
        var events = new List<string>();
        var providers = new IProvider[]
        {
            new TestProvider(
                "first",
                onStart: () =>
                {
                    events.Add("start:first");
                    cts.Cancel();
                },
                onStop: () => events.Add("stop:first")),
            new TestProvider("second", throwIfCanceledBeforeStart: true)
        };

        var exception = await Assert.ThrowsAsync<ProviderStartException>(() => ProviderLifecycleHelper.StartProvidersAsync(providers, cts.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception.StartException);
        Assert.Equal(new[] { "start:first", "stop:first" }, events);
    }

    [Fact]
    public async Task StartProvidersAsync_RollsBackStartedProviders_WhenLaterStartFails()
    {
        var events = new List<string>();
        var providers = new IProvider[]
        {
            new TestProvider("first", onStart: () => events.Add("start:first"), onStop: () => events.Add("stop:first")),
            new TestProvider("second", onStart: () => throw new InvalidOperationException("boom"))
        };

        var exception = await Assert.ThrowsAsync<ProviderStartException>(() => ProviderLifecycleHelper.StartProvidersAsync(providers));

        Assert.Equal("boom", exception.StartException.Message);
        Assert.Empty(exception.StopExceptions);
        Assert.Equal(new[] { "start:first", "stop:first" }, events);
    }

    [Fact]
    public async Task StopProvidersAsync_StopsProvidersInReverseOrder_AndCollectsFailures()
    {
        var events = new List<string>();
        var providers = new IProvider[]
        {
            new TestProvider("first", onStop: () => events.Add("stop:first")),
            new TestProvider("second", onStop: () => throw new InvalidOperationException("stop-fail")),
            new TestProvider("third", onStop: () => events.Add("stop:third"))
        };

        var exceptions = await ProviderLifecycleHelper.StopProvidersAsync(providers);

        Assert.Equal(new[] { "stop:third", "stop:first" }, events);
        var exception = Assert.Single(exceptions);
        Assert.Contains("second", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    private sealed class TestProvider : IProvider
    {
        private readonly Action? _onStart;
        private readonly Action? _onStop;
        private readonly bool _throwIfCanceledBeforeStart;

        public TestProvider(string name, Action? onStart = null, Action? onStop = null, bool throwIfCanceledBeforeStart = false)
        {
            Name = name;
            _onStart = onStart;
            _onStop = onStop;
            _throwIfCanceledBeforeStart = throwIfCanceledBeforeStart;
        }

        public string Name { get; }

        public event EventHandler<ActivityEvent>? EventProduced
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_throwIfCanceledBeforeStart)
                cancellationToken.ThrowIfCancellationRequested();
            _onStart?.Invoke();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _onStop?.Invoke();
            return Task.CompletedTask;
        }
    }
}

