using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     A tab on the main strip whose body is a rail of sections. The host draws the tab, the rail and the
///     selected section; the extension only declares it. A section joins it by naming <see cref="Id" /> as
///     the <c>HostId</c> of a tab descriptor the extension contributes through
///     <see cref="IExtensionContributions.Tabs" />, which gives each section its own view and view model.
///     The tab shows only while <see cref="FeatureId" /> is on and at least one of its sections is.
/// </summary>
/// <param name="Id">
///     The id sections name as their host, and the strip tab's id. Stored in the user's session, so it must
///     never change. It may not be a host id the app or another extension already uses.
/// </param>
/// <param name="Header">The strip header.</param>
/// <param name="Order">The strip position among the main tabs; the host's own tabs sit below 10.</param>
/// <param name="RailLabel">The band over the section rail.</param>
/// <param name="FeatureId">Shown only while this feature is on; null for while the extension is on.</param>
public sealed record HubTabContribution(string Id, string Header, int Order, string RailLabel, string? FeatureId = null)
{
    /// <summary>
    ///     State the extension keeps with the hub across launches, or null for none. Restored once, before
    ///     any section is shown, the first time the hub's feature is on in a session. Kept per extension: when
    ///     several of an extension's hubs carry one, only the first declared is kept.
    /// </summary>
    public IExtensionSessionState? Session { get; init; }
}

/// <summary>
///     A small piece of an extension's state the host writes into the user's session file and hands back on
///     the next launch. For layout and view choices, never for the user's work: a session file can be lost.
/// </summary>
public interface IExtensionSessionState
{
    /// <summary>The state to keep, or null to keep nothing. Called on the UI thread when the session is saved.</summary>
    JsonElement? Snapshot();

    /// <summary>
    ///     Applies state a previous launch kept. It may come from an older version of the extension, so read
    ///     each member on its own and ignore what is missing or malformed.
    /// </summary>
    /// <param name="state">The kept state.</param>
    void Restore(JsonElement state);
}

/// <summary>
///     The dot's meaning on a status chip. The host maps each value to a theme colour; the chip's label
///     carries the same meaning in words.
/// </summary>
public enum StatusChipDotState
{
    /// <summary>Idle or suspended.</summary>
    Off,

    /// <summary>Starting up or busy; usually pulsing.</summary>
    Working,

    /// <summary>Running and healthy.</summary>
    Good,

    /// <summary>Running, but something is uncertain.</summary>
    Degraded,

    /// <summary>Failed or lost.</summary>
    Error
}

/// <summary>
///     What a contributed status chip shows. The host draws the chip and reads these members on the UI
///     thread after each <see cref="INotifyPropertyChanged.PropertyChanged" />, which may be raised from any
///     thread. Any member name, or a null or empty one, re-reads them all.
/// </summary>
public interface IStatusChipSource : INotifyPropertyChanged
{
    /// <summary>True while the chip belongs on the status strip.</summary>
    bool IsShown { get; }

    /// <summary>The chip text. It carries the state in words; the dot only repeats it.</summary>
    string Label { get; }

    /// <summary>The dot's meaning.</summary>
    StatusChipDotState DotState { get; }

    /// <summary>True while the dot pulses, for work in flight.</summary>
    bool IsPulsing { get; }

    /// <summary>True to draw the dot as a ring: healthy as far as the extension can tell, but not confirmed.</summary>
    bool IsHollow { get; }

    /// <summary>The chip's tooltip, or null.</summary>
    string? Tooltip { get; }

    /// <summary>What a click on the chip runs, or null for a chip that only opens its flyout.</summary>
    ICommand? PrimaryAction { get; }

    /// <summary>
    ///     The view model shown in the chip's flyout, or null for no flyout. Resolved to a view by the same
    ///     naming rule as a tab's view model.
    /// </summary>
    object? FlyoutContent { get; }
}

/// <summary>A chip on the status strip, drawn by the host from the state the extension supplies.</summary>
/// <param name="Id">Unique across the app. Never shown.</param>
/// <param name="Source">The chip's state.</param>
/// <param name="FeatureId">Shown only while this feature is on; null for while the extension is on.</param>
public sealed record StatusChipContribution(string Id, IStatusChipSource Source, string? FeatureId = null);
