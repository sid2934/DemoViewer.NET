#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions.StratBook.ViewModels.Settings;
using DemoViewer.NET.Extensions.StratBook.Views.Settings;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The pack's two settings-page contributions: the Suggested Tags tuning card and the
///     Grenade Index card, both moved under Extensions. Each VM is built fresh per Settings open (the
///     tuning VM reads its stored report at construction), so the factories below must never be cached on
///     the contribution itself.
/// </summary>
internal static class StratBookSettingsPages
{
    /// <summary>
    ///     Suggested Tags tuning: hidden on the browser, where
    ///     <see cref="SuggestedTagsTuningViewModel.CanManageTuning" /> would be false anyway.
    /// </summary>
    public static SettingsPageContribution SuggestedTagsTuning(IServiceProvider sp) => new(
        "stratbook.suggested-tags-tuning",
        "SUGGESTED TAGS TUNING",
        0,
        "suggested tags tuning detectors profile execute default fake opener retake recall precision parameters preview verdicts",
        () => new SuggestedTagsTuningViewModel(
            sp.GetRequiredService<SuggestedTagsTuningService>(),
            sp.GetRequiredService<ProfileStore>(),
            OperatingSystem.IsBrowser()),
        () => new SuggestedTagsTuningView());

    /// <summary>
    ///     Grenade Index: relocated from HIGHLIGHTS, where it rode on that section's desktop-only gate
    ///     incidentally. Desktop only, same as before.
    /// </summary>
    public static SettingsPageContribution GrenadeIndex(IServiceProvider sp) => new(
        "stratbook.grenade-index",
        "GRENADE INDEX",
        1,
        "grenade index utility book lineup clip render walk background",
        () => new GrenadeIndexSettingsViewModel(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<IOptionsMonitor<AppSettings>>()),
        () => new GrenadeIndexSettingsView());
}
