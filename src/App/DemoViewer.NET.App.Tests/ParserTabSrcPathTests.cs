namespace DemoViewer.NET.AppTests;

/// <summary>
///     Regression pin for the four <c>ParserTabViewModel.SrcPath(...)</c> call sites that back the
///     Parser tab's clickable parse-chain source links (opened via <c>code --goto file:line</c>).
///     Each site was found pointing at a nonexistent path — either missing the "src/..." root
///     segment(s), or (for the two entity-tracking sites) still saying "Entities" from before the
///     entity-tracking code merged into "EntityTracking". A dead link fails silently (the click does
///     nothing), so this test independently resolves the repo root the same way
///     <c>MainViewModel.FindRepoRoot</c> does and asserts each composed target still exists — a
///     future file move turns the dead link into a red test instead of a silent nothing-happens click.
/// </summary>
public class ParserTabSrcPathTests
{
    [Test]
    [Arguments("AddPayloadNodeSteps → PayloadNodeBuilder.Build()/BuildFields()",
        "src/App/DemoViewer.NET/Models/PayloadNodeBuilder.cs")]
    [Arguments("AddPayloadNodeSteps → EntityTracker.PeekEntityUpdates() (entity_data step)",
        "src/Parser/CS2DemoKit.Parser/EntityTracking/EntityTracker.cs")]
    [Arguments("BuildChainForEntity → EntityTracker.ProcessFrame()/.../ReadEntityFields()",
        "src/Parser/CS2DemoKit.Parser/EntityTracking/EntityTracker.cs")]
    [Arguments("BuildChainForFrame → DemoParser.Parse()/ParseInnerMessages()/TryParseNetMessage()",
        "src/Parser/CS2DemoKit.Parser/DemoParser.cs")]
    public async Task SrcPathSite_ResolvesToExistingFile(string site, string repoRelativePath)
    {
        string repoRoot = FindRepoRoot();
        string[] parts = [repoRoot, .. repoRelativePath.Split('/')];
        string path = Path.Combine(parts);

        await Assert.That(File.Exists(path)).IsTrue()
            .Because($"{site} — SrcPath composes to \"{path}\", which must resolve to a real file");
    }

    /// <summary>Mirrors <c>MainViewModel.FindRepoRoot</c> (walks up from the test binary to the sentinel .slnx).</summary>
    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }
}
