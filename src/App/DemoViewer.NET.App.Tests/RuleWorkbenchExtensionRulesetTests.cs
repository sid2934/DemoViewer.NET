#region

using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Modules.RuleWorkbench;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     An extension's ruleset in the Authoring Workbench: listed beside the files with its owner, opened from the
///     YAML the extension supplies, read-only even in developer mode, checked and evaluable with the rest, and
///     hidden once a user file of its id overrides it.
/// </summary>
[NotInParallel]
public class RuleWorkbenchExtensionRulesetTests
{
    private const string Id = "dev_example_hello__kills";
    private const string Owner = "dev.example.hello";

    private const string Yaml = """
                                ruleset: dev_example_hello__kills
                                for: each_player
                                stats:
                                  kills:
                                    count: kill
                                    per: match
                                show:
                                  tables:
                                    hello_kills:
                                      per: player_match
                                      columns:
                                        - { stat: kills, label: kills }
                                """;

    [Test]
    public async Task AnExtensionsRuleset_IsListed_OpensWithItsYaml_AndStaysReadOnlyInDeveloperMode()
    {
        await With([new ContributedRuleset(Id, Owner, "pack.hello", () => Yaml)], _ => false, async (vm, _) =>
        {
            RulesetFileRef? file = vm.OpenableFiles.FirstOrDefault(f => f.IsExtension);
            await Assert.That(file).IsNotNull();
            await Assert.That(file!.Extension).IsEqualTo(Owner);
            await Assert.That(file.Display).Contains(Owner).And.Contains("off");
            await Assert.That(vm.EvaluableFiles.Any(f => f.FullPath == file.FullPath)).IsTrue();

            vm.SelectedFile = file;

            using (Assert.Multiple())
            {
                await Assert.That(vm.DocumentText).IsEqualTo(Yaml);
                await Assert.That(vm.IsExtensionFile).IsTrue();
                await Assert.That(vm.IsReadOnlyFile).IsTrue().Because("developer mode unlocks shipped files, never an extension's");
                await Assert.That(vm.IsDirty).IsFalse();
                await Assert.That(vm.IsClean).IsTrue().Because("the extension's ruleset composes with the shipped set");
            }
        });
    }

    [Test]
    public async Task SaveAsUnderItsId_OverridesTheExtensionsRuleset_AndHidesIt()
    {
        await With([new ContributedRuleset(Id, Owner, "pack.hello", () => Yaml)], null, async (vm, userDir) =>
        {
            vm.SelectedFile = vm.OpenableFiles.First(f => f.IsExtension);
            vm.DocumentText += "\n# my copy\n";
            vm.SaveCommand.Execute(null);
            await Assert.That(Directory.EnumerateFiles(userDir)).IsEmpty().Because("Save on an extension's ruleset never writes");

            await Assert.That(vm.SaveAsName).IsEqualTo(Id);
            vm.SaveAsCommand.Execute(null);

            using (Assert.Multiple())
            {
                await Assert.That(File.Exists(Path.Combine(userDir, Id + ".rules.yaml"))).IsTrue();
                await Assert.That(vm.OpenableFiles.Any(f => f.IsExtension)).IsFalse().Because("a user file of its id overrides it");
                await Assert.That(vm.SelectedFile?.IsExtension).IsEqualTo(false);
                await Assert.That(vm.IsReadOnlyFile).IsFalse();
            }
        });
    }

    [Test]
    public async Task ABrokenExtensionRuleset_IsListed_WithItsDiagnostics()
    {
        const string broken = "ruleset: dev_example_hello__kills\nfor: each_player\nstats:\n  kills:\n    count: no_such_event\n    per: match\n";
        await With([new ContributedRuleset(Id, Owner, "pack.hello", () => broken)], null, async (vm, _) =>
        {
            RulesetFileRef file = vm.OpenableFiles.First(f => f.IsExtension);
            vm.SelectedFile = file;

            await Assert.That(vm.IsClean).IsFalse();
            await Assert.That(vm.OpenFileDiagnostics).IsNotEmpty().Because("the author sees why the host left it out");
        });
    }

    [Test]
    public async Task AnExtensionRulesetThatTakesAShippedId_IsNotListed()
    {
        string shippedId = YamlConfigLoader.TryLoadDirectory(RuleSetLocator.ResolveShippedRulesDirectory()).Rulesets[0].Id;
        await With([new ContributedRuleset(shippedId, Owner, "pack.hello", () => "ruleset: " + shippedId + "\n")], null,
            async (vm, _) => await Assert.That(vm.OpenableFiles.Any(f => f.IsExtension)).IsFalse());
    }

    private static async Task With(IReadOnlyList<ContributedRuleset> rulesets, Func<ContributedRuleset, bool>? on,
        Func<RuleWorkbenchTabViewModel, string, Task> body)
    {
        string userDir = Directory.CreateTempSubdirectory("dvwb-ext-").FullName;
        AppSettings settings = new() { Features = new FeatureFlags { DeveloperMode = true } };
        RuleWorkbenchTabViewModel vm = new(new StubMonitor(settings), () => rulesets, on,
            (RuleSetLocator.ResolveShippedRulesDirectory(), userDir));
        vm.Dispose();
        try
        {
            await body(vm, userDir);
        }
        finally
        {
            Directory.Delete(userDir, true);
        }
    }

    private sealed class StubMonitor(AppSettings value) : IOptionsMonitor<AppSettings>
    {
        public AppSettings CurrentValue { get; } = value;
        public AppSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppSettings, string?> listener) => null;
    }
}
