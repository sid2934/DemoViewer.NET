namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>One of the user's accounts in Settings > Identity.</summary>
/// <param name="SteamId64">The account.</param>
/// <param name="Label">The user's own name for it, or null.</param>
/// <param name="LastName">Its most recently seen name in the library, RAW, or null when no demo holds it.</param>
/// <param name="DemoCount">How many demos hold it.</param>
/// <param name="IsActive">Whether it is the active account.</param>
public sealed record IdentityAccountRow(string SteamId64, string? Label, string? LastName, int DemoCount, bool IsActive)
{
    /// <summary>The label, else the last seen name, sanitized; else the SteamID.</summary>
    public string Title => Label ?? (string.IsNullOrEmpty(LastName) ? SteamId64 : DisplayText.Sanitize(LastName));

    /// <summary>The SteamID unless the title already is it, and how many demos hold it.</summary>
    public string Detail
    {
        get
        {
            string demos = DemoCount switch
            {
                0 => "in no demo yet",
                1 => "1 demo",
                _ => $"{DemoCount} demos"
            };
            return Title == SteamId64 ? demos : $"{SteamId64} · {demos}";
        }
    }
}
