#region

using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     What a pack adds to the 2D Playback tab. A contribution is a pair, an entry point and the surface
///     it opens (a band-menu entry and the pane its action shows), so the tab never has to know the
///     pane's view model to show it. Attached once per tab view-model instance while the owning pack
///     resolves on; detached when the pack goes off or the tab is disposed. After <see cref="Detach" />
///     nothing the contribution added may remain on the surface.
/// </summary>
public interface IPlaybackContribution
{
    /// <summary>Adds the contribution's entries and panes to <paramref name="surface" />.</summary>
    /// <param name="surface">The tab's surface; what is added through it is removed by <see cref="Detach" />.</param>
    /// <param name="context">The tab's module context, for <see cref="IModuleContext.GetService{T}" /> and the demo.</param>
    void Attach(IPlaybackSurface surface, IModuleContext context);

    /// <summary>Removes everything <see cref="Attach" /> added and drops the context. Idempotent.</summary>
    void Detach();
}
