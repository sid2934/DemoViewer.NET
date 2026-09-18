#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Hud;

/// <summary>
///     Supplies rasterised icon artwork to HUD layers.
///     <para>
///         <b>An interface rather than a reference, because Core is not allowed one.</b> Core's contract
///         is SkiaSharp plus the BCL and nothing else (<c>ArchitectureTests.Core_ReferencesOnlySkiaSharpAndBcl</c>),
///         so the assembly that owns the baked CS2 artwork cannot appear in its graph. Core states the
///         shape it needs; the heads that have the artwork hand one in.
///     </para>
///     <para>
///         <b>Optional by design.</b> A layer given no source falls back to its text tokens, which is what
///         keeps the existing export goldens valid: turning icons on for the exported HUD is a deliberate
///         act with a re-baseline attached, not a side effect of this type existing.
///     </para>
/// </summary>
public interface IIconSource
{
    /// <summary>
    ///     The artwork for one icon key, sized for a target draw height, or null when there is none —
    ///     an unknown key, or a kill CS2 ships no artwork for, such as an environment death.
    /// </summary>
    /// <param name="key">A namespace-qualified key, e.g. <c>equipment/ak47</c> or <c>modifier/headshot</c>.</param>
    /// <param name="pixelHeight">The height the icon will be drawn at; the source picks a master for it.</param>
    /// <returns>An image owned by the source, safe to draw and not to dispose.</returns>
    SKImage? Lookup(string key, float pixelHeight);

    /// <summary>
    ///     True when CS2 itself ships empty artwork for this key, so drawing nothing is the correct
    ///     outcome and a text fallback would invent meaning the game does not have.
    ///     <para>
    ///         Defaulted to false rather than abstract: a source that cannot tell the two absences apart
    ///         should treat every one as worth falling back to, and existing implementers keep compiling.
    ///     </para>
    /// </summary>
    /// <param name="key">A namespace-qualified key.</param>
    bool IsBlankByDesign(string key) => false;
}
