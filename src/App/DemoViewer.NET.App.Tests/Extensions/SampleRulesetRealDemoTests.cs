#region

using System.Reflection;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The sample extension's ruleset end to end on a real demo: contributed as YAML, read as the middle rule
///     layer, recorded by one forward read, stored by the core facts pass, and read back by the sample's own code
///     through <see cref="IAnalysisFacts" />. The sample loads the way a third party does, from a temp extensions
///     folder into its own context. Read in place from the <c>DEMO_PATH</c> folder.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class SampleRulesetRealDemoTests
{
    [Test]
    public async Task TheSample_ShipsARuleset_AndReadsItsOwnOutputBack()
    {
        string path = AppTests.BackgroundPlanRealDemoTests.SmallestDemo(0);
        string root = ExternalExtensionTests.NewRoot();
        try
        {
            ExternalExtensionTests.Install(root);
            await Run(path, ExternalExtensionTests.Resolve(root, allowUnverified: true).Loaded.Single().Pack);
        }
        finally
        {
            ExternalExtensionTests.Cleanup(root);
        }
    }

    private static async Task Run(string path, IExtension pack)
    {
        ServiceCollection services = new();
        services.AddSingleton<ExtensionShellHub>();
        services.AddSingleton(new DemoCacheStore(null));
        pack.Register(services);
        await using ServiceProvider sp = services.BuildServiceProvider();
        PackContributions contributions = new(pack, () => new ExtensionContext(pack, sp));
        pack.Contribute(contributions, sp);

        IReadOnlyList<ContributedRuleset> rulesets = contributions.Rulesets;
        string shipped = CS2DemoKit.Analysis.Yaml.RuleSetLocator.ResolveShippedRulesDirectory();
        MergedRulesBuild rules = new(() => RuleLayers.Load(shipped, null, rulesets, NullLogger.Instance),
        () =>
        [
            StampedRuleset.Core(RoundFactsFingerprint.RulesetId),
            .. rulesets.Select(r => new StampedRuleset(r.RulesetId, r.Owner, static () => true))
        ]);
        DemoCacheStore cache = sp.GetRequiredService<DemoCacheStore>();
        cache.Upsert(new DemoCacheRecord
        {
            Path = path,
            Size = new FileInfo(path).Length,
            Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 }
        });
        StampedFacts stamped = new(rules);
        FactsEvaluator facts = new(cache, stamped);

        IReadOnlySet<string> outputs = rules.StampedOutputs(id => id != RoundFactsFingerprint.RulesetId && facts.Records(path, id));
        using (DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None)))
        {
            facts.EvaluateForward(path, ForwardDemoPass.Run(reader, ForwardNeeds.Rules, rules.Docs, outputs: outputs));
        }

        AnalysisFacts library = new(cache, stamped, new RoundFactsSource(cache));
        Type helloFacts = pack.GetType().Assembly.GetType("HelloExtension.HelloFacts", true)!;
        FactKey key = (FactKey)helloFacts.GetProperty("Key", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        int? kills = (int?)helloFacts.GetMethod("TotalKills", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [library, path]);
        using (Assert.Multiple())
        {
            await Assert.That(rulesets.Select(r => r.RulesetId)).IsEquivalentTo(["dev_example_hello__kills"]);
            await Assert.That(outputs).IsEquivalentTo(["hello_kills"]);
            await Assert.That(library.Declared).Contains(key);
            await Assert.That(library.IsCurrent(path, key)).IsTrue();
            await Assert.That(kills ?? 0).IsGreaterThan(0).Because("the sample's own reader sums its table back");
            await Assert.That(rules.Fingerprint(64).Fingerprint).IsEqualTo(AppTests.MergedRulesBuildTests.ShippedFingerprint64);
        }
    }
}
