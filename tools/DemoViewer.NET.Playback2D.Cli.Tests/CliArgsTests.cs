#region

using DemoViewer.NET.Playback2D.Cli;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     The hand-rolled parser. Both repo styles must mean the same thing, and an unknown option must be
///     an error rather than a silent no-op — a typo in a CI golden invocation has to fail loudly.
/// </summary>
public class CliArgsTests
{
    private static readonly string[] _expectedLayers = ["radar", "markers", "vision"];

    [Test]
    public async Task SpaceAndEqualsForms_AreEquivalent()
    {
        CliArgs spaced = CliArgs.Parse(["render", "--out", "f.png"]);
        CliArgs equals = CliArgs.Parse(["render", "--out=f.png"]);

        await Assert.That(spaced.String("out")).IsEqualTo("f.png");
        await Assert.That(equals.String("out")).IsEqualTo("f.png");
    }

    [Test]
    public async Task BareFlag_IsTrue_AndDoesNotSwallowTheNextOption()
    {
        CliArgs args = CliArgs.Parse(["render", "--cpu", "--json", "--out", "f.png"]);

        await Assert.That(args.Flag("cpu")).IsTrue();
        await Assert.That(args.Flag("json")).IsTrue();
        await Assert.That(args.String("out")).IsEqualTo("f.png");
    }

    [Test]
    public async Task NegativeNumber_IsAValue_NotAFlag()
    {
        CliArgs args = CliArgs.Parse(["bench", "--speed", "-1.5"]);

        await Assert.That(args.Double("speed", 1)).IsEqualTo(-1.5);
    }

    [Test]
    public async Task VerbAndSubVerb_ComeFromThePositionals()
    {
        CliArgs args = CliArgs.Parse(["golden", "verify", "--json"]);

        await Assert.That(args.Verb).IsEqualTo("golden");
        await Assert.That(args.SubVerb).IsEqualTo("verify");
    }

    [Test]
    public async Task Terminator_MakesEverythingAfterItPositional()
    {
        CliArgs args = CliArgs.Parse(["render", "--", "--not-an-option"]);

        await Assert.That(args.Positional).Contains("--not-an-option");
        await Assert.That(args.Flag("not-an-option")).IsFalse();
    }

    [Test]
    public async Task Size_ParsesWxH()
    {
        CliArgs args = CliArgs.Parse(["render", "--size", "1920x1080"]);

        await Assert.That(args.Size("size", default)).IsEqualTo(new SKSizeI(1920, 1080));
    }

    [Test]
    public async Task Size_Malformed_Throws()
    {
        CliArgs args = CliArgs.Parse(["render", "--size", "1920"]);

        await Assert.That(Throws.Capture<CliUsageException>(() => args.Size("size", default))).IsNotNull();
    }

    [Test]
    public async Task Int_Malformed_Throws()
    {
        CliArgs args = CliArgs.Parse(["bench", "--frames", "many"]);

        await Assert.That(Throws.Capture<CliUsageException>(() => args.Int("frames", 1))).IsNotNull();
    }

    [Test]
    public async Task List_SplitsOnCommasAndTrims()
    {
        CliArgs args = CliArgs.Parse(["render", "--layers", "radar, markers ,vision"]);

        await Assert.That(args.List("layers")).IsEquivalentTo(_expectedLayers);
    }

    [Test]
    public async Task Require_Missing_Throws()
    {
        CliArgs args = CliArgs.Parse(["fixture", "capture"]);

        await Assert.That(Throws.Capture<CliUsageException>(() => args.Require("name"))).IsNotNull();
    }

    [Test]
    public async Task ThrowIfUnconsumed_UnknownOption_Throws()
    {
        CliArgs args = CliArgs.Parse(["render", "--fixture", "a.json", "--typoed", "1"]);
        args.String("fixture");

        await Assert.That(Throws.Capture<CliUsageException>(args.ThrowIfUnconsumed)).IsNotNull();
    }

    [Test]
    public async Task ThrowIfUnconsumed_EverythingRead_DoesNotThrow()
    {
        CliArgs args = CliArgs.Parse(["render", "--fixture", "a.json", "--cpu"]);
        args.String("fixture");
        args.Flag("cpu");

        args.ThrowIfUnconsumed();
        await Assert.That(args.Verb).IsEqualTo("render");
    }

    [Test]
    public async Task ShortHelp_IsRecognised()
    {
        await Assert.That(CliArgs.Parse(["-h"]).WantsHelp).IsTrue();
        await Assert.That(CliArgs.Parse(["render", "--help"]).WantsHelp).IsTrue();
    }
}
