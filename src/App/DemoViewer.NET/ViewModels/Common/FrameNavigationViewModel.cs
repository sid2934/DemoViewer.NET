#region

using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.ViewModels.Common;

/// <summary>
///     Thin shared state holder + navigation seam for frame / tick / round / special navigation.
///     Carries the navigation hooks
///     (<see cref="SeekToFrame" /> / <see cref="SeekToTick" /> / <see cref="RevealClass" />) the
///     command palette and the Output panel consume.
///     <para>
///         The design doc named these on an <c>IFrameNavigationService</c> interface. We instead hang them
///         on the existing shared <see cref="FrameNavigationViewModel" /> (already passed into every
///         tab VM ctor) as wired callback delegates — the same dependency direction as
///         <c>EntityTab.CreateTracker</c> / <c>ReplayTab.OnTickGroupSelected</c>. The shell wires the
///         concrete seek logic in its constructor; consumers take the VM, not an interface.
///     </para>
///     Side-effect orchestration (entity tracker advance, prev-tick snapshot, EntitiesRefreshed)
///     lives on <see cref="EntityTracking.EntityTrackingTabViewModel" />, not here.
/// </summary>
public sealed class FrameNavigationViewModel : ObservableObject
{
    /// <summary>Switches to the Entity Tracking tab and filters/selects the given class name.</summary>
    public Action<string>? RevealClassHandler { get; set; }

    // ── Navigation hooks — wired by MainViewModel ctor ──────────────

    /// <summary>Selects the frame at the given 0-based index. No-op if out of range.</summary>
    public Action<int>? SeekToFrameHandler { get; set; }

    /// <summary>Selects the first frame at or after the given server tick. No-op if not found.</summary>
    public Action<int>? SeekToTickHandler { get; set; }

    /// <summary>Reveals <paramref name="className" /> in the Entity Tracking tab via the wired shell handler.</summary>
    public void RevealClass(string className) => RevealClassHandler?.Invoke(className);

    /// <summary>Selects the frame at <paramref name="frameIndex" /> via the wired shell handler.</summary>
    public void SeekToFrame(int frameIndex) => SeekToFrameHandler?.Invoke(frameIndex);

    /// <summary>Seeks to the first frame at or after <paramref name="tick" /> via the wired shell handler.</summary>
    public void SeekToTick(int tick) => SeekToTickHandler?.Invoke(tick);

    /// <summary>Fires when the user seeks to a new frame. Tab VMs subscribe to do their own rebuilds.</summary>
    public event Action<int>? SelectedFrameChanged;

    /// <summary>Internal helper for sub-step migrations to raise the event from MainViewModel.</summary>
    internal void RaiseSelectedFrameChanged(int frameIndex) => SelectedFrameChanged?.Invoke(frameIndex);
}
