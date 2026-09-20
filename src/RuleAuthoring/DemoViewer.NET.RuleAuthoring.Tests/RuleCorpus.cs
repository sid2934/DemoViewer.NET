namespace DemoViewer.NET.RuleAuthoring.Tests;

/// <summary>
///     Finds the repository's shipped <c>rules/</c> directory from the test binary's location.
///     <para>
///         Walked up rather than copied to the output, because the point of the round-trip gate is
///         that it runs against the files that actually ship. A copied fixture would go stale the
///         first time somebody edited a real ruleset, and would do it silently.
///     </para>
/// </summary>
internal static class RuleCorpus
{
    /// <summary>Every shipped <c>*.rules.yaml</c>, sorted so the test order is stable.</summary>
    internal static IReadOnlyList<string> ShippedFiles()
    {
        string[] files = Directory.GetFiles(RulesDirectory(), "*.rules.yaml", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0)
        {
            throw new InvalidOperationException($"no rulesets under {RulesDirectory()}");
        }

        return files;
    }

    /// <summary>The repository's <c>rules/</c> directory.</summary>
    internal static string RulesDirectory()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "rules");
            if (System.IO.Directory.Exists(candidate)
                && System.IO.Directory.GetFiles(candidate, "*.rules.yaml").Length > 0)
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"no rules/ directory above {AppContext.BaseDirectory}");
    }
}
