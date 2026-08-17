#region

using Cs2DemoKit.Parser;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Verifies the cheap header-only metadata reader (
///     <see cref="DownstreamUtilities.TryReadQuickInfo(string,out DownstreamUtilities.DemoQuickInfo)" />)
///     used by the demo-library indexer's instant tier: it must recover the map name from a real demo without
///     a full parse, and reject non-demo input. Skips if no demo is present.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class DemoQuickInfoTests
{
    [Test]
    public async Task ReadsMap_FromRealDemo_WithoutFullParse()
    {
        string? path = DemoTestHelper.FindDemoPath("vitality-vs-fut-m2-dust2.dem")
                       ?? DemoTestHelper.FindDemoPath("vitality-vs-fut-m3-nuke.dem");
        if (path is null)
        {
            throw new SkipTestException("no demo present");
        }

        bool ok = DownstreamUtilities.TryReadQuickInfo(path, out DownstreamUtilities.DemoQuickInfo info);
        Console.WriteLine($"[quickinfo] ok={ok} map={info.MapName} server={info.ServerName} ver={info.DemoVersion}");

        await Assert.That(ok).IsTrue();
        await Assert.That(info.MapName).IsNotNull();
        await Assert.That(info.MapName).StartsWith("de_"); // real competitive map
    }

    [Test]
    public async Task RejectsNonDemoBytes()
    {
        byte[] garbage = new byte[512];
        for (int i = 0; i < garbage.Length; i++)
        {
            garbage[i] = (byte)(i * 7);
        }

        bool ok = DownstreamUtilities.TryReadQuickInfo(garbage.AsSpan(), out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task RejectsTruncatedInput()
    {
        bool ok = DownstreamUtilities.TryReadQuickInfo("PBDEMS2\0"u8.ToArray().AsSpan(), out _);
        await Assert.That(ok).IsFalse(); // valid magic but no frame
    }
}
