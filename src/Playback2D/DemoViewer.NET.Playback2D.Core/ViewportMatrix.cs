#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     The one place a <see cref="ViewportTransform" /> becomes an <see cref="SKMatrix" />.
///     <para>
///         <b>Almost nothing should use this.</b> Dynamic layers transform their own points
///         (<c>transform.WorldToScreen</c> per point) exactly as the pre-v2 control did, because setting
///         a world→screen matrix on the canvas would also scale stroke widths and marker radii and
///         break pixel parity outright (plan decision D-8). The matrix exists for the two cases that
///         genuinely want it: replaying a world-space <c>Static</c> picture under the current camera,
///         and any future world-space clip.
///     </para>
/// </summary>
public static class ViewportMatrix
{
    /// <summary>
    ///     World → pane-local screen, matching <see cref="ViewportTransform.WorldToScreen" /> exactly.
    ///     Y is negated because world Y is up and screen Y is down.
    /// </summary>
    /// <param name="transform">The camera.</param>
    public static SKMatrix From(ViewportTransform transform)
    {
        float s = (float)transform.EffectiveScale;
        float tx = (float)(-transform.CenterX * transform.EffectiveScale + transform.ViewWidth / 2 + transform.PanX);
        float ty = (float)(transform.CenterY * transform.EffectiveScale + transform.ViewHeight / 2 + transform.PanY);
        return new SKMatrix(s, 0, tx, 0, -s, ty, 0, 0, 1);
    }
}
