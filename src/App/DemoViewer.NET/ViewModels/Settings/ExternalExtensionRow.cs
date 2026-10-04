#region

using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>One third-party extension under the extensions folder, as Settings lists it.</summary>
/// <param name="Name">Its name, or its folder when the manifest did not parse.</param>
/// <param name="Version">Its version, or empty.</param>
/// <param name="State">Loaded and how it was trusted, or why it did not load.</param>
/// <param name="IsLoaded">True when it is running.</param>
/// <param name="IsUnverified">True when it runs, or would, without a verified signature.</param>
public sealed record ExternalExtensionRow(string Name, string Version, string State, bool IsLoaded, bool IsUnverified)
{
    /// <summary>The loaded third-party extensions first, then the copies that did not load.</summary>
    public static IReadOnlyList<ExternalExtensionRow> Build(IReadOnlyList<PackStatus> statuses, IReadOnlyList<LoadOutcome> rejected)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(rejected);
        List<ExternalExtensionRow> rows = [];
        foreach (PackStatus status in statuses)
        {
            if (status.Source is not PackSource.External external)
            {
                continue;
            }

            rows.Add(new ExternalExtensionRow(status.Manifest?.Name ?? status.Pack.Id, status.Manifest?.Version.ToString() ?? "",
                external.Verified ? "Loaded. Verified." : "Loaded. Unverified: it runs with the same access to your files as the app.",
                true, !external.Verified));
        }

        foreach (LoadOutcome outcome in rejected)
        {
            string name = outcome.Manifest?.Name ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(outcome.Directory));
            rows.Add(new ExternalExtensionRow(name, outcome.Manifest?.Version.ToString() ?? "", "Not loaded: " + outcome.Detail,
                false, outcome.Failure == LoadFailure.Unverified));
        }

        return rows;
    }
}
