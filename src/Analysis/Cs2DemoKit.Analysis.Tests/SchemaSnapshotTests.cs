namespace Cs2DemoKit.Analysis.Tests;

/// <summary>
///     Pins the <c>cs2-opendocs</c> submodule to a checked-in SHA. The submodule
///     is the source of truth for our parser's schema (via <c>CS2OpenDev-SDK</c>
///     which regenerates <c>SchemaNames.X.Y</c> constants from
///     <c>cs2-opendocs/data/Protobufs/</c>). When Valve renames or moves a field
///     upstream, the constant changes silently and code that depended on the
///     old name compiles, runs, but reads <c>null</c> from every entity. Phase
///     3's <c>m_hActiveWeapon</c> bug was a preview of that failure mode.
///     <para>
///         This test catches schema drift at submodule level — the test fails
///         loudly when the active submodule SHA differs from
///         <c>tests/fixtures/cs2-opendocs.expected-sha</c>, naming both SHAs
///         in the failure message and pointing at the regeneration procedure.
///     </para>
///     <para>
///         <b>Refresh procedure</b> when the drift is intentional:
///         <list type="number">
///             <item><c>git submodule update --remote cs2-opendocs</c> (or check out the new SHA)</item>
///             <item><c>dotnet run --project tools/DemoViewer.NET.Codegen</c> to regenerate <c>SchemaNames</c></item>
///             <item>Run the bench-suite and audit for stat divergences</item>
///             <item>
///                 Update <c>tests/fixtures/cs2-opendocs.expected-sha</c> with the new SHA, commit alongside the code
///                 changes
///             </item>
///         </list>
///     </para>
///     <para>
///         Complement to the schema-keys assertion,
///         which catches changes that flow through SDK
///         regeneration before anyone updates the pinned SHA.
///     </para>
/// </summary>
[Category("Unit")]
public class SchemaSnapshotTests
{
    /// <summary>Cs2 opendocs submodule_pinned to expected sha.</summary>
    [Test]
    public async Task Cs2OpendocsSubmodule_PinnedToExpectedSha()
    {
        string? repoRoot = FindRepoRoot();
        await Assert.That(repoRoot)
            .IsNotNull()
            .Because("test must run from within the repo (cannot find DemoViewer.NET.slnx walking up from assembly base)");

        string expectedShaPath = Path.Combine(repoRoot!, "tests", "fixtures", "cs2-opendocs.expected-sha");
        await Assert.That(File.Exists(expectedShaPath))
            .IsTrue()
            .Because($"Expected SHA pin file missing: {expectedShaPath}. " +
                     "Add the SHA from `git submodule status cs2-opendocs` to that file.");

        string expectedSha = File.ReadAllText(expectedShaPath).Trim();
        await Assert.That(expectedSha.Length).IsEqualTo(40)
            .Because($"Expected SHA must be a 40-char Git hash, got {expectedSha.Length} chars: '{expectedSha}'");

        string actualSha = ReadSubmoduleHeadSha(repoRoot!, "cs2-opendocs")
                           ?? throw new InvalidOperationException(
                               "Could not read cs2-opendocs submodule HEAD — is the submodule initialised? " +
                               "Run `git submodule update --init --recursive`.");

        if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("cs2-opendocs submodule drifted:");
            Console.WriteLine($"  expected: {expectedSha}");
            Console.WriteLine($"  actual  : {actualSha}");
            Console.WriteLine();
            Console.WriteLine("If this is intentional (Valve schema update merged):");
            Console.WriteLine("  1. Regenerate SchemaNames: dotnet run --project tools/DemoViewer.NET.Codegen");
            Console.WriteLine("  2. Run bench-suite and audit for stat divergences");
            Console.WriteLine($"  3. Update tests/fixtures/cs2-opendocs.expected-sha to {actualSha}");
        }

        await Assert.That(actualSha).IsEqualTo(expectedSha);
    }

    private static string? FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "DemoViewer.NET.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static bool IsHex(string s)
    {
        foreach (char c in s)
        {
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f' || c is >= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Reads the submodule HEAD SHA by parsing <c>.git/modules/&lt;name&gt;/HEAD</c>
    ///     directly rather than shelling out to <c>git</c>. Avoids a process spawn
    ///     and works even when the test runner doesn't have <c>git</c> on PATH.
    /// </summary>
    private static string? ReadSubmoduleHeadSha(string repoRoot, string submoduleName)
    {
        // A submodule's HEAD lives at <repo>/.git/modules/<name>/HEAD.
        // Content is either a 40-char SHA on its own line, or "ref: refs/heads/..."
        // pointing at a ref file we'd need to dereference.
        string headPath = Path.Combine(repoRoot, ".git", "modules", submoduleName, "HEAD");
        if (!File.Exists(headPath))
        {
            // Fall back to reading the submodule's own .git pointer file.
            string subGitPointer = Path.Combine(repoRoot, submoduleName, ".git");
            if (!File.Exists(subGitPointer))
            {
                return null;
            }

            // .git file content: "gitdir: ../.git/modules/<name>"
            string relGitDir = File.ReadAllText(subGitPointer).Trim();
            if (relGitDir.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                relGitDir = relGitDir["gitdir:".Length..].Trim();
            }

            string absGitDir = Path.IsPathRooted(relGitDir)
                ? relGitDir
                : Path.GetFullPath(Path.Combine(repoRoot, submoduleName, relGitDir));
            headPath = Path.Combine(absGitDir, "HEAD");
            if (!File.Exists(headPath))
            {
                return null;
            }
        }

        string headContent = File.ReadAllText(headPath).Trim();

        // Direct SHA: 40 hex chars.
        if (headContent.Length == 40 && IsHex(headContent))
        {
            return headContent.ToLowerInvariant();
        }

        // Symbolic ref: "ref: refs/heads/main" → read <git-dir>/refs/heads/main
        if (headContent.StartsWith("ref:", StringComparison.Ordinal))
        {
            string refPath = headContent["ref:".Length..].Trim();
            string gitDir = Path.GetDirectoryName(headPath)!;
            string refFile = Path.Combine(gitDir, refPath);
            if (File.Exists(refFile))
            {
                string sha = File.ReadAllText(refFile).Trim();
                if (sha.Length == 40 && IsHex(sha))
                {
                    return sha.ToLowerInvariant();
                }
            }

            // Packed refs fallback: read <git-dir>/packed-refs.
            string packedRefs = Path.Combine(gitDir, "packed-refs");
            if (File.Exists(packedRefs))
            {
                foreach (string line in File.ReadAllLines(packedRefs))
                {
                    if (line.Length < 42 || line.StartsWith('#'))
                    {
                        continue;
                    }

                    int sp = line.IndexOf(' ');
                    if (sp != 40)
                    {
                        continue;
                    }

                    if (line[(sp + 1)..].Trim() == refPath)
                    {
                        return line[..40].ToLowerInvariant();
                    }
                }
            }
        }

        return null;
    }
}
