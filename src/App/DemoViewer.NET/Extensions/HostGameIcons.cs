#region

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.GameIcons;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The app behind the UI kit's <see cref="GameIcon" />: the baked catalogue, which ships inside the
///     application rather than in the SDK packages because the artwork is Valve's.
/// </summary>
internal sealed class HostGameIcons : IGameIconBackend
{
    /// <summary>
    ///     Installed when the assembly is first used, so a test that never builds the Avalonia app still
    ///     draws icons through the same path the app does.
    /// </summary>
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255",
        Justification = "Application code that is a class library only because several heads reference it; "
                        + "the UI kit's icons must be live before any extension view draws one.")]
    internal static void Install() => GameIcon.Backend ??= new HostGameIcons();

    /// <inheritdoc />
    public GameIconMetrics? Resolve(string? key, out GameIconAvailability availability)
    {
        IconRef? icon = IconCatalogue.Demand(key, out IconAvailability catalogued);
        availability = catalogued switch
        {
            IconAvailability.Available => GameIconAvailability.Available,
            IconAvailability.Blank => GameIconAvailability.Blank,
            IconAvailability.Missing => GameIconAvailability.Missing,
            _ => GameIconAvailability.None
        };

        return icon is null ? null : new GameIconMetrics(icon.Key, icon.Aspect, icon.Tintable);
    }

    /// <inheritdoc />
    public int ScaleFor(double pixelHeight) => IconCatalogue.ScaleFor(pixelHeight);

    /// <inheritdoc />
    public Stream Open(string key, int scale) =>
        IconCatalogue.Get(key)?.Open(scale)
        ?? throw new ArgumentOutOfRangeException(nameof(key), key, "not a baked icon key");
}
