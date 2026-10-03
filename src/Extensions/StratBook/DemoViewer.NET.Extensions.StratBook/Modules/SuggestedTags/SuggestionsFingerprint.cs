#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     The detector-set fingerprint (suggested-tags.md §3.4): SHA-256 of the profile's JSON, the site
///     region table in force for the map, and the detector code version. Any of the three moving marks
///     every proposals file on the map stale, so <see cref="SuggestedTagsService.Wants" /> asks for a
///     rebuild; verdicts are keyed by proposal identity and are untouched.
/// </summary>
public static class SuggestionsFingerprint
{
    /// <summary>
    ///     Bumped whenever a detector's rule or the proposals file shape changes in a way the profile and
    ///     the regions cannot express: every demo's proposals are then rebuilt once.
    /// </summary>
    public const int DetectorCodeVersion = 1;

    /// <summary>The fingerprint, lowercase hex.</summary>
    /// <param name="profile">The profile in force.</param>
    /// <param name="table">The shipped or learned table the map's regions compose from, or null for site-only.</param>
    public static string Compose(DetectorProfile profile, SiteRegionTable? table)
    {
        ArgumentNullException.ThrowIfNull(profile);
        StringBuilder text = new();
        text.Append("suggested-tags/")
            .Append(DetectorCodeVersion.ToString(CultureInfo.InvariantCulture))
            .Append('/')
            .Append(ProposalDocument.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture))
            .Append('\n')
            .Append(profile.ToJson())
            .Append('\n')
            .Append(table?.ToJson() ?? "site-only");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
