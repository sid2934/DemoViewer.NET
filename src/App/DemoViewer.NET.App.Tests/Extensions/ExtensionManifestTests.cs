#region

using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="ExtensionManifest" /> parsing: the required members, the value rules, unknown members
///     ignored, and the shipped <c>src/Extensions/StratBook/extension.json</c>, read from the repo, from the
///     copy beside the test binary and from the embedded resource, all describing <see cref="StratBookPack" />.
/// </summary>
public class ExtensionManifestTests
{
    private const string Good = """
        {
          "id": "net.demoviewer.pack.example",
          "name": "Example",
          "version": "1.2.3",
          "assembly": "Example.dll",
          "entryType": "Example.ExamplePack",
          "requiresHost": "^1.0",
          "requiresCs2DemoKit": "0.13.0-beta0001",
          "minAppVersion": "0.9.0"
        }
        """;

    [Test]
    public async Task Parse_ReadsEveryMember()
    {
        ExtensionManifest m = ExtensionManifest.Parse(Good);
        using (Assert.Multiple())
        {
            await Assert.That(m.Id).IsEqualTo("net.demoviewer.pack.example");
            await Assert.That(m.Name).IsEqualTo("Example");
            await Assert.That(m.Version).IsEqualTo(SemVersion.Parse("1.2.3"));
            await Assert.That(m.Assembly).IsEqualTo("Example.dll");
            await Assert.That(m.EntryType).IsEqualTo("Example.ExamplePack");
            await Assert.That(m.RequiresHost.ToString()).IsEqualTo("^1.0");
            await Assert.That(m.RequiresCs2DemoKit.ToString()).IsEqualTo("0.13.0-beta0001");
            await Assert.That(m.MinAppVersion).IsEqualTo(SemVersion.Parse("0.9.0"));
        }
    }

    [Test]
    public async Task Parse_MinAppVersionIsOptional_UnknownMembersAreIgnored_CommentsAndTrailingCommasAllowed()
    {
        ExtensionManifest m = ExtensionManifest.Parse("""
            {
              // a newer manifest may carry members this app does not know
              "id": "net.demoviewer.pack.example",
              "name": "Example",
              "version": "1.0.0",
              "assembly": "Example.dll",
              "entryType": "Example.ExamplePack",
              "requiresHost": ">=1.0.0 <2.0.0",
              "requiresCs2DemoKit": "*",
              "signature": "abc",
              "feed": { "url": "https://example.invalid" },
            }
            """);
        using (Assert.Multiple())
        {
            await Assert.That(m.MinAppVersion).IsNull();
            await Assert.That(m.RequiresCs2DemoKit.ToString()).IsEqualTo("*");
        }
    }

    [Test]
    [Arguments("id")]
    [Arguments("name")]
    [Arguments("version")]
    [Arguments("assembly")]
    [Arguments("entryType")]
    [Arguments("requiresHost")]
    [Arguments("requiresCs2DemoKit")]
    public async Task Parse_FailsWhenARequiredMemberIsMissing(string member)
    {
        string json = string.Join('\n', Good.Split('\n').Where(line => !line.Contains($"\"{member}\"", StringComparison.Ordinal)));
        ExtensionManifestException ex = Assert.Throws<ExtensionManifestException>(() => ExtensionManifest.Parse(json));
        await Assert.That(ex.Message).Contains(member);
    }

    [Test]
    [Arguments("\"version\": \"1.2.3\"", "\"version\": \"1.2\"", "version")]
    [Arguments("\"version\": \"1.2.3\"", "\"version\": \"\"", "version")]
    [Arguments("\"requiresHost\": \"^1.0\"", "\"requiresHost\": \"latest\"", "requiresHost")]
    [Arguments("\"requiresCs2DemoKit\": \"0.13.0-beta0001\"", "\"requiresCs2DemoKit\": \">=\"", "requiresCs2DemoKit")]
    [Arguments("\"minAppVersion\": \"0.9.0\"", "\"minAppVersion\": \"nine\"", "minAppVersion")]
    [Arguments("\"assembly\": \"Example.dll\"", "\"assembly\": \"../Example.dll\"", "assembly")]
    [Arguments("\"assembly\": \"Example.dll\"", "\"assembly\": \"Example.exe\"", "assembly")]
    [Arguments("\"id\": \"net.demoviewer.pack.example\"", "\"id\": \"example\"", "id")]
    [Arguments("\"id\": \"net.demoviewer.pack.example\"", "\"id\": \"net.demo viewer\"", "id")]
    [Arguments("\"id\": \"net.demoviewer.pack.example\"", "\"id\": \".evil.pack\"", "id")]
    [Arguments("\"id\": \"net.demoviewer.pack.example\"", "\"id\": \"../net.demoviewer\"", "id")]
    [Arguments("\"id\": \"net.demoviewer.pack.example\"", "\"id\": \"net.demoviewer/pack\"", "id")]
    [Arguments("\"id\": \"net.demoviewer.pack.example\"", "\"id\": \"net.demoviewer:pack\"", "id")]
    public async Task Parse_FailsOnABadValue(string original, string replacement, string member)
    {
        string json = Good.Replace(original, replacement, StringComparison.Ordinal);
        ExtensionManifestException ex = Assert.Throws<ExtensionManifestException>(() => ExtensionManifest.Parse(json));
        await Assert.That(ex.Message).Contains(member);
    }

    [Test]
    public async Task Parse_FailsOnMalformedJson_AndOnNull()
    {
        using (Assert.Multiple())
        {
            Assert.Throws<ExtensionManifestException>(() => ExtensionManifest.Parse("{ not json"));
            Assert.Throws<ExtensionManifestException>(() => ExtensionManifest.Parse("null"));
            Assert.Throws<ExtensionManifestException>(() => ExtensionManifest.Parse("[]"));
            await Assert.That(Assert.Throws<ExtensionManifestException>(() => ExtensionManifest.Parse("{}")).Message).Contains("required");
        }
    }

    // The repo copy is a template: the build stamps "{nbgv}" with the version from version.json
    // (CompatibilityMatrixTests covers the stamping itself).
    [Test]
    public async Task TheManifestTemplate_InTheRepo_DescribesStratBookPack()
    {
        string repoRoot = DemoTestHelper.FindRepoRoot()
            ?? throw new SkipTestException("repo root not found (no DemoViewer.NET.slnx above the test binary)");
        string path = Path.Combine(repoRoot, "src", "Extensions", "StratBook", ExtensionManifest.FileName);
        string template = await File.ReadAllTextAsync(path);
        ExtensionManifest m = ExtensionManifest.Parse(template.Replace("\"{nbgv}\"", "\"0.0.0\"", StringComparison.Ordinal));
        await AssertIsStratBook(m);
    }

    [Test]
    public async Task TheShippedManifest_IsCopiedBesideTheExtensionAssembly()
    {
        // The csproj's None item flows through the project reference, so the test binary's directory holds
        // the copy a loader would read before loading the assembly.
        string path = Path.Combine(AppContext.BaseDirectory, ExtensionManifest.FileName);
        await Assert.That(File.Exists(path)).IsTrue().Because($"{path} should be copied on build");
        await AssertIsStratBook(ExtensionManifest.Parse(await File.ReadAllTextAsync(path)));
    }

    [Test]
    public async Task TheShippedManifest_IsEmbedded_AndIsWhatThePackReports()
    {
        ExtensionManifest embedded = ExtensionManifest.ReadEmbedded(typeof(StratBookPack).Assembly);
        using (Assert.Multiple())
        {
            await AssertIsStratBook(embedded);
            await Assert.That(new StratBookPack().Manifest).IsEqualTo(embedded);
        }
    }

    [Test]
    public async Task ReadEmbedded_FailsForAnAssemblyWithoutOne()
    {
        ExtensionManifestException ex = Assert.Throws<ExtensionManifestException>(() => ExtensionManifest.ReadEmbedded(typeof(ExtensionManifestTests).Assembly));
        await Assert.That(ex.Message).Contains(ExtensionManifest.FileName);
    }

    private static async Task AssertIsStratBook(ExtensionManifest m)
    {
        using (Assert.Multiple())
        {
            await Assert.That(m.Id).IsEqualTo(StratBookPack.PackId);
            await Assert.That(m.Name).IsEqualTo("Strat Book");
            await Assert.That(m.Assembly).IsEqualTo(typeof(StratBookPack).Assembly.GetName().Name + ".dll");
            await Assert.That(m.EntryType).IsEqualTo(typeof(StratBookPack).FullName);
            await Assert.That(Type.GetType(m.EntryType + ", " + typeof(StratBookPack).Assembly.GetName().Name)).IsEqualTo(typeof(StratBookPack))
                .Because("a loader instantiates the entry type by name");
        }
    }
}
