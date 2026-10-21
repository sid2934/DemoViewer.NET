#region

using System.Globalization;
using System.Text.RegularExpressions;

#endregion

namespace DemoViewer.NET.Services.Players;

/// <summary>Which spelling of an account <see cref="SteamIdParser" /> recognised.</summary>
public enum SteamIdForm
{
    SteamId64,
    ProfileUrl,
    SteamId3,
    SteamId2,

    /// <summary>A bare number below 2^32: the account id, which a name could equally be.</summary>
    AccountId,

    /// <summary>A <c>/id/&lt;name&gt;</c> URL, which only Steam can turn into an account.</summary>
    VanityUrl
}

/// <summary>What <see cref="SteamIdParser.Parse" /> made of the text.</summary>
/// <param name="Form">The spelling.</param>
/// <param name="SteamId64">The account, null for a vanity URL.</param>
/// <param name="Vanity">The vanity name of a <see cref="SteamIdForm.VanityUrl" />, else null.</param>
public sealed record SteamIdParse(SteamIdForm Form, ulong? SteamId64, string? Vanity = null)
{
    /// <summary>True for the one form that is a guess: a bare account id.</summary>
    public bool IsFlagged => Form == SteamIdForm.AccountId;
}

/// <summary>Reads the SteamID spellings a person pastes: SteamID64, profile URLs, <c>[U:1:n]</c>, <c>STEAM_X:Y:Z</c> and a bare account id.</summary>
public static partial class SteamIdParser
{
    /// <summary>The SteamID64 of account id 0 in the public universe: individual, instance 1.</summary>
    public const ulong IndividualBase = 76561197960265728UL;

    [GeneratedRegex(@"^\[?U:1:(?<n>\d{1,10})(:\d+)?\]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamId3Pattern();

    [GeneratedRegex(@"^STEAM_[0-5]:(?<y>[01]):(?<z>\d{1,10})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamId2Pattern();

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?steamcommunity\.com/(?<kind>profiles|id)/(?<v>[^/?#\s]+)/?(?:[?#].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfileUrlPattern();

    /// <summary>The recognised spelling, or null when the text is none of them.</summary>
    /// <param name="text">What the person pasted.</param>
    public static SteamIdParse? Parse(string? text)
    {
        string input = text?.Trim() ?? "";
        if (input.Length == 0)
        {
            return null;
        }

        if (ProfileUrlPattern().Match(input) is { Success: true } url)
        {
            string value = url.Groups["v"].Value;
            if (url.Groups["kind"].Value.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                return new SteamIdParse(SteamIdForm.VanityUrl, null, Uri.UnescapeDataString(value));
            }

            // Steam serves a profile under its SteamID64 and under its [U:1:n] too.
            if (IsSteamId64(value, out ulong profile))
            {
                return new SteamIdParse(SteamIdForm.ProfileUrl, profile);
            }

            return SteamId3Pattern().Match(value) is { Success: true } inUrl
                   && FromAccountId(inUrl.Groups["n"].Value, SteamIdForm.ProfileUrl) is { } parsed
                ? parsed
                : null;
        }

        if (SteamId3Pattern().Match(input) is { Success: true } id3)
        {
            return FromAccountId(id3.Groups["n"].Value, SteamIdForm.SteamId3);
        }

        if (SteamId2Pattern().Match(input) is { Success: true } id2
            && ulong.TryParse(id2.Groups["z"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong z))
        {
            ulong account = z * 2 + (id2.Groups["y"].Value == "1" ? 1UL : 0UL);
            return account is > 0 and <= uint.MaxValue ? new SteamIdParse(SteamIdForm.SteamId2, IndividualBase + account) : null;
        }

        if (IsSteamId64(input, out ulong id))
        {
            return new SteamIdParse(SteamIdForm.SteamId64, id);
        }

        return input.All(char.IsAsciiDigit) ? FromAccountId(input, SteamIdForm.AccountId) : null;
    }

    /// <summary>True for an individual account in the public universe: account ids 1 to 2^32 - 1.</summary>
    /// <param name="steamId64">The account.</param>
    public static bool IsIndividual(ulong steamId64) => steamId64 > IndividualBase && steamId64 - IndividualBase <= uint.MaxValue;

    private static SteamIdParse? FromAccountId(string digits, SteamIdForm form) =>
        ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out ulong account) && account is > 0 and <= uint.MaxValue
            ? new SteamIdParse(form, IndividualBase + account)
            : null;

    private static bool IsSteamId64(string text, out ulong id) =>
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > uint.MaxValue && text.All(char.IsAsciiDigit);
}
