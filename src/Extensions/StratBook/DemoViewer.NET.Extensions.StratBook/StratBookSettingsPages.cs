#region

using DemoViewer.NET.Extensions.StratBook.ViewModels.Settings;
using DemoViewer.NET.Extensions.StratBook.Views.Settings;
using DemoViewer.NET.Modules.SuggestedTags;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The pack's settings page with controls of its own: the Suggested Tags tuning card. Its VM is built
///     fresh per Settings open (it reads its stored report at construction), so the factory must never be
///     cached on the contribution itself.
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
}
