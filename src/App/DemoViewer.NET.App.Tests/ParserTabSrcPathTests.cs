namespace DemoViewer.NET.AppTests;

/// <summary>
///     Regression pin for the <c>ParserTabViewModel.SrcPath(...)</c> call sites that back the
///     Parser tab's clickable parse-chain source links (opened via <c>code --goto file:line</c>).
///     Each site was once found pointing at a nonexistent path, and a dead link fails silently —
///     the click just does nothing. This test resolves the repo root the same way
///     <c>MainViewModel.FindRepoRoot</c> does and asserts each composed target still exists, so a
///     file move turns the dead link into a red test.
///     <para>
///         Only App-owned files are pinned here. The parse pipeline and entity decoder ship as the
///         CS2DemoKit packages, so their sources are not in this checkout; those chain entries link
///         out to the upstream repository on GitHub and cannot be checked against the filesystem.
///     </para>
/// </summary>
public class ParserTabSrcPathTests
{
    [Test]
    [Arguments("AddPayloadNodeSteps → PayloadNodeBuilder.Build()/BuildFields()",
        "src/App/DemoViewer.NET/Models/PayloadNodeBuilder.cs")]
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
