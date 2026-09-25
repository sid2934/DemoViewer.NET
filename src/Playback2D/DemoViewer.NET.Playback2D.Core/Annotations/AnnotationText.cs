#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Annotations;

/// <summary>
///     How a <see cref="AnnotationKind.Text" /> element is sized and where its box lies, in one place, so
///     the layer that draws it and the eraser that hits it cannot disagree about either.
///     <para>
///         The em size is <see cref="SizePerWidth" /> × <see cref="AnnotationStyle.WidthWorld" /> world
///         units (step-authoring.md §3.2, owner decision 9): text follows the pen and zooms with the map
///         like ink. It is shaped once at <see cref="ReferenceSizePx" /> and scaled, so a width slider
///         dragged through a hundred fractional values shapes each string once rather than once per
///         width, and the blob cache keeps one sized font.
///     </para>
///     <para>
///         The anchor is the top-left corner of the text's line box as it reads on screen: the text hangs
///         below and to the right of the point that was pressed, where the host's editor sits.
///     </para>
/// </summary>
public static class AnnotationText
{
    /// <summary>World em size per world unit of stroke width.</summary>
    public const float SizePerWidth = 6f;

    /// <summary>The em size strings are shaped at before scaling to world units.</summary>
    public const float ReferenceSizePx = 64f;

    /// <summary>The world em size for a stroke width. Never negative.</summary>
    /// <param name="widthWorld">The element's <see cref="AnnotationStyle.WidthWorld" />.</param>
    public static float WorldSize(float widthWorld) => SizePerWidth * Math.Max(0f, widthWorld);

    /// <summary>World units per reference pixel: the scale the shaped blob is drawn at.</summary>
    /// <param name="widthWorld">The element's <see cref="AnnotationStyle.WidthWorld" />.</param>
    public static float Scale(float widthWorld) => WorldSize(widthWorld) / ReferenceSizePx;

    /// <summary>
    ///     The world rectangle the text's line box covers, from the same measurement the layer draws with.
    ///     <c>Top</c> is the LOWER world Y (world Y points up; the text hangs below its anchor). False for an
    ///     element with no point or no text.
    /// </summary>
    /// <param name="cache">The blob cache to shape through.</param>
    /// <param name="element">A text element.</param>
    /// <param name="bounds">The world box, when there is one.</param>
    public static bool TryWorldBounds(TextBlobCache cache, AnnotationElement element, out SKRect bounds)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(element);

        bounds = SKRect.Empty;
        if (element.Points.Count == 0 || cache.Get(element.Text, ReferenceSizePx) is not { } shaped)
        {
            return false;
        }

        InkPoint anchor = element.Points[0];
        float scale = Scale(element.Style.WidthWorld);
        bounds = new SKRect(anchor.X, anchor.Y - shaped.Height * scale, anchor.X + shaped.Width * scale,
            anchor.Y);
        return true;
    }
}
