#region

using System.Security;

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     Resolves the bundled sample demo (<c>assets/tour/*.dem</c> — the trimmed three-round match the
///     Library's "Try a sample match" CTA and the first-run walkthrough open when the user has no demos
///     of their own). App-only (the sample demo is a product asset, not a library concern), but it follows
///     the same shipped-asset convention as the packaged <c>CollisionAssetLocator</c>:
///     <c>scripts/publish.sh</c> copies the committed <c>assets/</c> tree next to the binary, so the same
///     walk-up finds it in a dev checkout and an installed build alike.
///     <para>
///         <b>Resolution order</b> (first hit wins; mirrors <c>CollisionAssetLocator</c>):
///         <list type="number">
///             <item>
///                 <c>DEMOVIEWER_TOUR_DEMO</c> env var — <b>authoritative</b> when set: an existing
///                 <c>.dem</c> path wins outright, anything else (including a deliberately bogus path)
///                 resolves to "no sample" with no fallback, so it doubles as a disable switch.
///             </item>
///             <item>
///                 Walk up from <see cref="AppContext.BaseDirectory" /> to the first
///                 <c>assets/tour/</c> holding a <c>.dem</c>; the ordinal-first file is the sample
///                 (there ships exactly one — nothing keys on the filename).
///             </item>
///         </list>
///         No resolvable sample → <c>null</c>, never a throw — callers degrade (the CTA hides, the
///         walkthrough gateway falls back to the Open-Demo button). Browser/WASM has no filesystem to
///         walk, so it lands on <c>null</c> through the same path.
///     </para>
/// </summary>
public static class TourDemoLocator
{
    /// <summary>The env var naming a specific sample-demo file (authoritative override / disable switch).</summary>
    public const string EnvVar = "DEMOVIEWER_TOUR_DEMO";

    /// <summary>
    ///     Returns the absolute path of the bundled sample demo, or null when none is resolvable.
    ///     Never throws.
    /// </summary>
    public static string? FindSampleDemo()
    {
        try
        {
            string? env = Environment.GetEnvironmentVariable(EnvVar);
            if (!string.IsNullOrWhiteSpace(env))
            {
                return File.Exists(env) ? env : null;
            }

            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string tour = Path.Combine(dir.FullName, "assets", "tour");
                if (Directory.Exists(tour))
                {
                    string? sample = Directory.EnumerateFiles(tour, "*.dem")
                        .OrderBy(p => p, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (sample is not null)
                    {
                        return sample;
                    }
                }

                dir = dir.Parent;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or SecurityException or UnauthorizedAccessException)
        {
            // An unreadable directory / hostile override is a "no sample", not a crash.
        }

        return null;
    }
}
