#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Views.Highlights;

/// <summary>
///     The cross-demo <c>Add clips</c> picker body (docs/ui/highlights-matchoverview-redesign.md).
///     Hosted as an OVERLAY inside the Reels dashboard rather than a window: a second window would need
///     <c>IWindowService</c> — the surface the reel modal's retirement is stripping — and would be
///     unreachable on the browser host.
///     <para>
///         No code-behind logic. Escape-to-close is handled by the hosting overlay in
///         <see cref="HighlightsTabView" /> (it owns the focus scope); everything else is bound.
///     </para>
/// </summary>
public partial class AddClipsPickerView : UserControl
{
    /// <summary>Builds the view.</summary>
    public AddClipsPickerView() => InitializeComponent();
}
