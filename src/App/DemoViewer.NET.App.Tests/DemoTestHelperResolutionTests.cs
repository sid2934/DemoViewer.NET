#region

using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The <c>DEMO_PATH</c> resolution rules in <see cref="DemoTestHelper.ResolveDemoPathOverride" />,
///     exercised against temp folders with the environment passed in, never set: every RealDemo test
///     in this process reads the real <c>DEMO_PATH</c>, and a test that mutated it would race them.
///     No real demo is read; the files here are empty and only their names matter.
/// </summary>
public class DemoTestHelperResolutionTests
{
    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), $"dv-demopath-{Guid.NewGuid():N}");

    private static string Touch(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, []);
        return path;
    }

    [Test]
    public async Task UnsetOrBlank_ResolvesToNothing_WithNoProblem()
    {
        foreach (string? env in new[] { null, "", "   " })
        {
            string? path = DemoTestHelper.ResolveDemoPathOverride(env, null, out string? problem);
            await Assert.That(path).IsNull();
            await Assert.That(problem).IsNull();
        }
    }

    [Test]
    public async Task File_IsReturnedAsGiven_AndThePickIsIgnored()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            string demo = Touch(root, "one.dem");
            string? path = DemoTestHelper.ResolveDemoPathOverride(demo, "other.dem", out string? problem);
            await Assert.That(path).IsEqualTo(demo);
            await Assert.That(problem).IsNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task MissingPath_ResolvesToNothing_AndSaysSo()
    {
        string missing = Path.Combine(TempRoot(), "nowhere.dem");
        string? path = DemoTestHelper.ResolveDemoPathOverride(missing, null, out string? problem);
        await Assert.That(path).IsNull();
        await Assert.That(problem).Contains("DEMO_PATH does not exist");
    }

    /// <summary>
    ///     Ordinal, not culture or creation order: "B" sorts before "a" and a name with a later
    ///     match id sorts after one with an earlier id, whichever was written first. A sidecar
    ///     ".dem.info", the shape the Steam replays folder keeps beside every demo, is never a pick.
    /// </summary>
    [Test]
    public async Task Folder_WithoutPick_TakesTheOrdinalFirstDem_TopLevelOnly()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            Touch(root, "match730_002.dem");
            Touch(root, "a.dem.info");
            Touch(root, "match730_001.dem.info");
            string expected = Touch(root, "B.dem");
            Touch(root, "a.dem");
            Touch(root, "match730_001.dem");
            Touch(root, "notes.txt");
            string nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(nested);
            Touch(nested, "A.dem");

            string? path = DemoTestHelper.ResolveDemoPathOverride(root, null, out string? problem);
            await Assert.That(path).IsEqualTo(expected);
            await Assert.That(problem).IsNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Folder_WithPick_TakesThePick_AheadOfTheOrdinalFirst()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            Touch(root, "a.dem");
            string expected = Touch(root, "z.dem");

            string? path = DemoTestHelper.ResolveDemoPathOverride(root, "z.dem", out string? problem);
            await Assert.That(path).IsEqualTo(expected);
            await Assert.That(problem).IsNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     A pick that is not there must not fall back to the first file: the run was pinned to one
    ///     demo on purpose, and a silent swap is the failure mode this helper exists to remove.
    /// </summary>
    [Test]
    public async Task Folder_WithMissingPick_ResolvesToNothing_AndNamesThePick()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            Touch(root, "a.dem");

            string? path = DemoTestHelper.ResolveDemoPathOverride(root, "gone.dem", out string? problem);
            await Assert.That(path).IsNull();
            await Assert.That(problem).Contains("DEMO_PATH_PICK 'gone.dem'");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     The reference demo is pinned everywhere else in the chain so demo-agnostic tests see one
    ///     structural shape across machines; a folder that holds it gets the same pick.
    /// </summary>
    [Test]
    public async Task Folder_HoldingTheReferenceDemo_TakesIt_EvenWhenItIsNotFirst()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            Touch(root, "000_first.dem");
            string expected = Touch(root, DemoTestHelper.ReferenceDemoFileName);

            string? path = DemoTestHelper.ResolveDemoPathOverride(root, null, out string? problem);
            await Assert.That(path).IsEqualTo(expected);
            await Assert.That(problem).IsNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task EmptyFolder_ResolvesToNothing_AndSaysSo()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            Touch(root, "only.dem.info");

            string? path = DemoTestHelper.ResolveDemoPathOverride(root, null, out string? problem);
            await Assert.That(path).IsNull();
            await Assert.That(problem).Contains("holds no .dem");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
