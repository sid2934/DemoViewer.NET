#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Reading colours back out of a captured frame.
///     <para>
///         Shared because the channel-order trap below is the kind that costs an afternoon once and
///         should cost nobody a second one, and a copy of it in each test file is a copy that can drift.
///     </para>
/// </summary>
public static class FrameProbe
{
    /// <summary>
    ///     The frame as bytes, NORMALISED to B,G,R,A whatever the platform framebuffer declares.
    ///     <para>
    ///         <b>The headless Skia surface hands back RGBA here, not BGRA.</b> The older render helpers
    ///         never noticed because they only ask <c>r > 60 || g > 60 || b > 60</c>, which is
    ///         channel-order independent. A colour assertion is not: with red and blue transposed,
    ///         <c>#4CAF50</c> still matches (its red and blue are four apart) while <c>#5FA894</c> finds
    ///         nothing. The failure is silent and looks like the feature is broken.
    ///     </para>
    /// </summary>
    public static byte[] ToBytes(WriteableBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        PixelSize size = bitmap.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];
        PixelFormat? format;
        using (ILockedFramebuffer fb = bitmap.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
            format = fb.Format;
        }

        if (format != PixelFormat.Rgba8888)
        {
            return buffer;
        }

        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            (buffer[i], buffer[i + 2]) = (buffer[i + 2], buffer[i]);
        }

        return buffer;
    }

    /// <summary>
    ///     Counts pixels NEAR one RGB value in a normalised frame. Near, not exact: small anti-aliased
    ///     glyphs and thin round-capped strokes leave few fully-covered pixels at the pure value.
    ///     <para>
    ///         Keep <paramref name="tolerance" /> well inside the smallest gap between the colours being
    ///         told apart, or a hit for one becomes a near-miss of another.
    ///     </para>
    /// </summary>
    public static int CountPixels(byte[] buffer, uint rgb, int tolerance = 20)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        int wantR = (byte)(rgb >> 16), wantG = (byte)(rgb >> 8), wantB = (byte)rgb;
        int hits = 0;
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            if (Math.Abs(buffer[i] - wantB) <= tolerance
                && Math.Abs(buffer[i + 1] - wantG) <= tolerance
                && Math.Abs(buffer[i + 2] - wantR) <= tolerance)
            {
                hits++;
            }
        }

        return hits;
    }
}
