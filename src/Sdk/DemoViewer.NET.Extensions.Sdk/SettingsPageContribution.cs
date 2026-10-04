using Avalonia.Controls;

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>A Settings page. The view model and the view are built the first time the page is shown.</summary>
/// <param name="Id">Unique across the app.</param>
/// <param name="Header">The navigation entry.</param>
/// <param name="Order">Position among pages; the host's pages use 0 to 99.</param>
/// <param name="Keywords">Extra words the Settings search matches.</param>
/// <param name="ViewModelFactory">Builds the page's view model, the view's DataContext.</param>
/// <param name="ViewFactory">Builds the page's view.</param>
/// <param name="FeatureId">Shown only while this feature is on; null for while the extension is on.</param>
public sealed record SettingsPageContribution(
    string Id,
    string Header,
    int Order,
    string Keywords,
    Func<object> ViewModelFactory,
    Func<Control> ViewFactory,
    string? FeatureId = null);
