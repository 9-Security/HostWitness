using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinDFIR.Providers.Parsers;
using Xunit;

namespace WinDFIR.Tests;

public class WmiPersistenceParserTests
{
    // --- Synthetic tests (no sample needed): the ASCII-run instance patterns ---

    [Fact]
    public void ExtractFromRuns_RecoversMaliciousBindingFilterConsumer()
    {
        // Simulates the readable runs an actual malicious subscription leaves in OBJECTS.DATA.
        var runs = new List<string>
        {
            "__EventFilter", @"root\cimv2", "EvilFilter",
            "SELECT * FROM __InstanceModificationEvent WITHIN 60", "WQL",
            "__FilterToConsumerBinding",
            "CommandLineEventConsumer.Name=\"EvilConsumer\"",
            "__EventFilter.Name=\"EvilFilter\"",
            "CommandLineEventConsumer.Name=\"EvilConsumer\"" // consumer instance elsewhere
        };

        var records = WmiPersistenceParser.ExtractFromRuns(runs);

        var binding = records.SingleOrDefault(r => r.Kind == "Binding");
        Assert.NotNull(binding);
        Assert.Equal("CommandLineEventConsumer", binding!.ConsumerClass);
        Assert.Equal("EvilConsumer", binding.ConsumerName);
        Assert.Equal("EvilFilter", binding.FilterName);

        var filter = records.SingleOrDefault(r => r.Kind == "Filter");
        Assert.NotNull(filter);
        Assert.Equal("EvilFilter", filter!.Name);
        Assert.Contains("__InstanceModificationEvent", filter.Query);
        Assert.Equal("WQL", filter.QueryLanguage);

        Assert.Contains(records, r => r.Kind == "Consumer" && r.ConsumerName == "EvilConsumer");
    }

    [Fact]
    public void ExtractFromRuns_IgnoresClassDefinitionNoise()
    {
        // Class-definition runs: property declarations, no .Name="..." values or query text.
        var runs = new List<string>
        {
            "__FilterToConsumerBinding", "Association", "Consumer.f", "ref:__EventConsumer",
            "CreatorSID", "uint8", "boolean", "Filter.f", "ref:__EventFilter",
            "__EventFilter", "CreatorSID", "EventAccess", "string", "Query", "QueryLanguage", "string"
        };

        var records = WmiPersistenceParser.ExtractFromRuns(runs);
        Assert.Empty(records); // pure schema -> nothing reported
    }

    [Fact]
    public void ExtractAsciiRuns_PullsPrintableRuns()
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes("__EventFilter").Concat(new byte[] { 0, 1, 2 })
            .Concat(System.Text.Encoding.ASCII.GetBytes("ab")) // too short
            .Concat(new byte[] { 0 })
            .Concat(System.Text.Encoding.ASCII.GetBytes("root\\cimv2")).ToArray();

        var runs = WmiPersistenceParser.ExtractAsciiRuns(bytes);
        Assert.Contains("__EventFilter", runs);
        Assert.Contains(@"root\cimv2", runs);
        Assert.DoesNotContain("ab", runs); // below min length
    }

    // --- Ground-truth validation against the real OBJECTS.DATA (gated) ---

    private const string ObjectsData = @"D:\cursor\KDFIR\KAPE\KAPE_Extracted\C\Windows\System32\wbem\Repository\OBJECTS.DATA";

    [Fact]
    public void Parse_RealRepository_RecoversScmDefaultSubscription_NotSchemaNoise()
    {
        if (!File.Exists(ObjectsData))
            return;

        var records = WmiPersistenceParser.Parse(ObjectsData);

        // The benign default SCM Event Log subscription must be recovered.
        var binding = records.FirstOrDefault(r => r.Kind == "Binding" && r.ConsumerName == "SCM Event Log Consumer");
        Assert.NotNull(binding);
        Assert.Equal("NTEventLogEventConsumer", binding!.ConsumerClass);
        Assert.Equal("SCM Event Log Filter", binding.FilterName);

        var filter = records.FirstOrDefault(r => r.Kind == "Filter" && r.Name == "SCM Event Log Filter");
        Assert.NotNull(filter);
        Assert.Contains("MSFT_SCMEventLogEvent", filter!.Query);
        Assert.Equal("WQL", filter.QueryLanguage);

        // Schema noise (12 CommandLineEventConsumer class-def hits, etc.) must NOT explode into records.
        Assert.True(records.Count(r => r.Kind == "Binding") <= 3, $"Too many bindings (schema noise?): {records.Count(r => r.Kind == "Binding")}");
    }
}
