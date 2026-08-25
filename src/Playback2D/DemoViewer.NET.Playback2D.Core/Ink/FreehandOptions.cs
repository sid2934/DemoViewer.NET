// Adapted from perfect-freehand v1.2.2 (MIT, © 2021 Stephen Ruiz Ltd).
// See THIRD-PARTY-NOTICES.md § "perfect-freehand (MIT)" for the full licence text and the list of
// adapted files. Upstream: https://github.com/steveruizok/perfect-freehand

namespace DemoViewer.NET.Playback2D.Core.Ink;

/// <summary>
///     Stroke-shaping options, matching perfect-freehand's <c>StrokeOptions</c> for the subset this port
///     exposes. Doubles throughout, because upstream's numbers are IEEE doubles and the reference vectors
///     are generated from it — a float here would show up as a mismatch that looks like a porting bug.
/// </summary>
/// <param name="Size">Base stroke diameter in the same units as the input points (world units here).</param>
/// <param name="Thinning">
///     How much pressure affects width, −1..1. 0 disables thinning entirely and pins the radius at
///     <paramref name="Size" />/2.
/// </param>
/// <param name="Smoothing">
///     Outline point decimation: a candidate offset point is dropped unless it is further than
///     <c>(Size · Smoothing)²</c> from the previous one. 0 keeps every point.
/// </param>
/// <param name="Streamline">Input smoothing 0..1. Each sample is lerped toward the previous one.</param>
/// <param name="SimulatePressure">
///     Derive pressure from velocity instead of trusting the device. Upstream's default, and the correct
///     choice for a mouse (which reports no pressure at all).
/// </param>
/// <param name="CapStart">Round cap at the start; false gives a flat end.</param>
/// <param name="TaperStart">Taper length at the start, in stroke units. 0 = none.</param>
/// <param name="CapEnd">Round cap at the end; false gives a flat end.</param>
/// <param name="TaperEnd">Taper length at the end, in stroke units. 0 = none.</param>
public readonly record struct FreehandOptions(
    double Size,
    double Thinning,
    double Smoothing,
    double Streamline,
    bool SimulatePressure,
    bool CapStart,
    double TaperStart,
    bool CapEnd,
    double TaperEnd)
{
    /// <summary>Upstream's defaults: size 16, thinning/smoothing/streamline 0.5, simulated pressure, caps on.</summary>
    public static readonly FreehandOptions Default = new(16, 0.5, 0.5, 0.5, true, true, 0, true, 0);

    /// <summary>The same options at a different stroke width.</summary>
    /// <param name="size">The new stroke diameter.</param>
    public FreehandOptions WithSize(double size) =>
        this with
        {
            Size = size
        };

    /// <summary>
    ///     The options an <c>AnnotationStyle</c>'s world-space width maps to. The ONE place the mapping
    ///     lives, so the eraser's hit-test outline and the layer's drawn outline are the same polygon.
    /// </summary>
    /// <param name="widthWorld">Stroke width in world units. Clamped away from zero.</param>
    public static FreehandOptions ForWidth(double widthWorld) => Default.WithSize(Math.Max(0.01, widthWorld));
}

/// <summary>
///     One streamlined input sample with the derived facts the outline pass needs: the unit vector back
///     toward the previous point, the distance travelled since it, and the running arc length.
/// </summary>
/// <param name="X">Streamlined X.</param>
/// <param name="Y">Streamlined Y.</param>
/// <param name="Pressure">Pressure 0..1 as reported (the outline pass may re-derive it).</param>
/// <param name="VectorX">Unit vector X, pointing from this point back to the previous one.</param>
/// <param name="VectorY">Unit vector Y, pointing from this point back to the previous one.</param>
/// <param name="Distance">Distance from the previous accepted point.</param>
/// <param name="RunningLength">Arc length from the first point.</param>
public readonly record struct StrokePoint(
    double X,
    double Y,
    double Pressure,
    double VectorX,
    double VectorY,
    double Distance,
    double RunningLength);
