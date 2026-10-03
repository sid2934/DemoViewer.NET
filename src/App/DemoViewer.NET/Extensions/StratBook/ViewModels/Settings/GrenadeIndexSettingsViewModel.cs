#region

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Configuration;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>
///     The Grenade Index settings card (grenade-walk.md §3.8): the library's background walk opt-in and the
///     Lineup Clip Render opt-in. Its own small VM, not a settings-page contribution, because a contributed
///     page cannot reach <c>SettingsViewModel</c>'s private <c>Persist</c>/<c>Reflect</c> echo-guard pair;
///     this one owns an identical pair over the same <see cref="SettingsService" /> singleton.
/// </summary>
public sealed partial class GrenadeIndexSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsService _settings;
    private readonly IDisposable? _onChange;
    private bool _applyingExternal;
    private bool _disposed;
    private bool _writing;

    /// <summary>Background library grenade walk opt-in → <c>AppSettings.Grenades.BackgroundIndex</c> (default OFF).</summary>
    [ObservableProperty]
    private bool _backgroundIndex;

    /// <summary>Lineup Clip Render opt-in → <c>AppSettings.Grenades.RenderLineupClips</c> (default ON).</summary>
    [ObservableProperty]
    private bool _renderLineupClips;

    /// <param name="settings">The live settings service, shared with the rest of Settings.</param>
    /// <param name="monitor">Its bound options monitor, for external-change reflection.</param>
    public GrenadeIndexSettingsViewModel(SettingsService settings, IOptionsMonitor<AppSettings> monitor)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(monitor);
        _settings = settings;

        AppSettings current = settings.Current;
        _backgroundIndex = current.Grenades.BackgroundIndex;
        _renderLineupClips = current.Grenades.RenderLineupClips;

        _onChange = monitor.OnChange((updated, _) => OnSettingsChanged(updated));
    }

    partial void OnBackgroundIndexChanged(bool value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Grenades.BackgroundIndex = value);
    }

    partial void OnRenderLineupClipsChanged(bool value)
    {
        if (_applyingExternal)
        {
            return;
        }

        Persist(s => s.Grenades.RenderLineupClips = value);
    }

    private void Persist(Action<AppSettings> mutate)
    {
        if (_disposed)
        {
            return;
        }

        _writing = true;
        try
        {
            _settings.Write(mutate);
        }
        finally
        {
            _writing = false;
        }
    }

    // External change (another surface / a hand-edit). Self-writes raise this too, synchronously, while
    // _writing is set, so they are skipped as a redundant echo.
    private void OnSettingsChanged(AppSettings settings)
    {
        if (_writing || _disposed)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Reflect(settings);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Reflect(settings));
        }
    }

    private void Reflect(AppSettings settings)
    {
        if (_disposed)
        {
            return;
        }

        _applyingExternal = true;
        try
        {
            BackgroundIndex = settings.Grenades.BackgroundIndex;
            RenderLineupClips = settings.Grenades.RenderLineupClips;
        }
        finally
        {
            _applyingExternal = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _onChange?.Dispose();
    }
}
