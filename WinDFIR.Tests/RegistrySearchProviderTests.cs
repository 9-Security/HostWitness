using Microsoft.Win32;
using WinDFIR.Core.Entities;
using WinDFIR.Providers;
using Xunit;

namespace WinDFIR.Tests;

public class RegistrySearchProviderTests
{
    [Fact]
    public async Task RunQueriesAsync_EmptyList_CompletesWithoutThrow()
    {
        var provider = new RegistrySearchProvider();
        await provider.RunQueriesAsync(Array.Empty<RegistryQuery>(), default);
    }

    [Fact]
    public async Task RunQueriesAsync_NonExistentKey_CompletesWithoutThrow()
    {
        var provider = new RegistrySearchProvider();
        var queries = new[]
        {
            new RegistryQuery
            {
                Name = "NonExistent",
                Hive = RegistryHive.CurrentUser,
                KeyPath = "NonExistentKeyPath_ShouldNotExist_12345"
            }
        };
        await provider.RunQueriesAsync(queries, default);
    }

    [Fact]
    public async Task StartAsync_AfterAddDefaultQueries_CompletesWithoutThrow()
    {
        var provider = new RegistrySearchProvider();
        provider.AddDefaultQueries();
        await provider.StartAsync(default);
    }

    /// <summary>
    /// Regression: opens a standard HKCU subkey, exercises Live Registry + RegQueryInfoKey LastWriteTime path (see TECH_DEBT §1). Windows only.
    /// </summary>
    [Fact]
    public async Task RunQueriesAsync_HkcuEnvironment_ProducesRegistryCategoryEvents()
    {
        var provider = new RegistrySearchProvider();
        var events = new List<ActivityEvent>();
        provider.EventProduced += (_, e) => events.Add(e);

        var queries = new[]
        {
            new RegistryQuery
            {
                Name = "Regression HKCU Environment",
                Hive = RegistryHive.CurrentUser,
                KeyPath = "Environment"
            }
        };

        await provider.RunQueriesAsync(queries, default);

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal("Registry", e.Category));
    }
}
