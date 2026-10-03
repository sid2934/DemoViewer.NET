#region

using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="SemVersion" /> parsing and precedence, and <see cref="VersionRange" /> over the syntax a
///     manifest may use: comparator sets, caret, tilde, X-ranges, exact, any, and <c>||</c>. The prerelease
///     rule matters most: the CS2DemoKit pin is a prerelease, so an exact prerelease must match itself while
///     a plain range must not quietly admit a prerelease of a later core.
/// </summary>
public class VersionRangeTests
{
    [Test]
    public async Task SemVersion_ParsesStrictly_AndRoundTrips()
    {
        using (Assert.Multiple())
        {
            await Assert.That(SemVersion.Parse("1.2.3").ToString()).IsEqualTo("1.2.3");
            await Assert.That(SemVersion.Parse("0.13.0-beta0001+9f1e").ToString()).IsEqualTo("0.13.0-beta0001")
                .Because("build metadata is dropped");
            await Assert.That(SemVersion.Parse("1.0.0-rc.1").Prerelease).IsEqualTo("rc.1");
            await Assert.That(SemVersion.TryParse("1.2", out _)).IsFalse().Because("a version needs three components");
            await Assert.That(SemVersion.TryParse("01.2.3", out _)).IsFalse().Because("no leading zeros");
            await Assert.That(SemVersion.TryParse("1.2.3.4", out _)).IsFalse().Because("four components is not SemVer");
            await Assert.That(SemVersion.TryParse("", out _)).IsFalse();
            await Assert.That(SemVersion.TryParse(null, out _)).IsFalse();
            Assert.Throws<FormatException>(() => SemVersion.Parse("x"));
        }
    }

    [Test]
    public async Task SemVersion_ParsesTheNbgvInformationalShape()
    {
        using (Assert.Multiple())
        {
            // What NBGV stamps on CS2DemoKit.Analysis: four numbers, a prerelease, build metadata.
            await Assert.That(SemVersion.TryParseInformational("0.13.0.1-beta0001+9f1e3e3b4a", out SemVersion? v)).IsTrue();
            await Assert.That(v!.ToString()).IsEqualTo("0.13.0-beta0001");
            await Assert.That(SemVersion.TryParseInformational("1.2.3", out SemVersion? plain)).IsTrue();
            await Assert.That(plain!.ToString()).IsEqualTo("1.2.3");
            await Assert.That(SemVersion.TryParseInformational("1.2.3.4", out SemVersion? four)).IsTrue();
            await Assert.That(four!.ToString()).IsEqualTo("1.2.3");
            await Assert.That(SemVersion.TryParseInformational(null, out _)).IsFalse();
        }
    }

    [Test]
    public async Task SemVersion_OrdersBySemVerPrecedence()
    {
        string[] ascending =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11",
            "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"
        ];
        SemVersion[] parsed = [.. ascending.Select(SemVersion.Parse)];
        using (Assert.Multiple())
        {
            for (int i = 1; i < parsed.Length; i++)
            {
                await Assert.That(parsed[i - 1] < parsed[i]).IsTrue().Because($"{ascending[i - 1]} < {ascending[i]}");
                await Assert.That(parsed[i] > parsed[i - 1]).IsTrue();
            }

            await Assert.That(SemVersion.Parse("1.0.0-beta0001")).IsEqualTo(SemVersion.Parse("1.0.0-beta0001+other"))
                .Because("records compare by value and metadata is not a value");
            await Assert.That(SemVersion.Parse("1.0.0") >= SemVersion.Parse("1.0.0")).IsTrue();
            await Assert.That(SemVersion.Parse("1.0.0") <= SemVersion.Parse("1.0.0")).IsTrue();
        }
    }

    [Test]
    public async Task Range_ComparatorSets()
    {
        VersionRange range = VersionRange.Parse(">=1.0.0 <2.0.0");
        using (Assert.Multiple())
        {
            await Assert.That(range.Satisfies(SemVersion.Parse("1.0.0"))).IsTrue();
            await Assert.That(range.Satisfies(SemVersion.Parse("1.9.9"))).IsTrue();
            await Assert.That(range.Satisfies(SemVersion.Parse("2.0.0"))).IsFalse();
            await Assert.That(range.Satisfies(SemVersion.Parse("0.9.9"))).IsFalse();
            await Assert.That(range.ToString()).IsEqualTo(">=1.0.0 <2.0.0").Because("the range prints as written");
            await Assert.That(VersionRange.Parse(">1.0.0").Satisfies(SemVersion.Parse("1.0.0"))).IsFalse();
            await Assert.That(VersionRange.Parse("<=1.0.0").Satisfies(SemVersion.Parse("1.0.0"))).IsTrue();
            await Assert.That(VersionRange.Parse("=1.0.0").Satisfies(SemVersion.Parse("1.0.1"))).IsFalse();
        }
    }

    [Test]
    public async Task Range_Caret_Tilde_XRange_Any()
    {
        using (Assert.Multiple())
        {
            VersionRange caret = VersionRange.Parse("^1.0");
            await Assert.That(caret.Satisfies(SemVersion.Parse("1.0.0"))).IsTrue();
            await Assert.That(caret.Satisfies(SemVersion.Parse("1.7.2"))).IsTrue();
            await Assert.That(caret.Satisfies(SemVersion.Parse("2.0.0"))).IsFalse();
            await Assert.That(caret.Satisfies(SemVersion.Parse("0.9.0"))).IsFalse();

            // Below 1.0 the caret pins the minor, the SemVer convention for pre-1.0 breaking changes.
            VersionRange zeroCaret = VersionRange.Parse("^0.13.0");
            await Assert.That(zeroCaret.Satisfies(SemVersion.Parse("0.13.4"))).IsTrue();
            await Assert.That(zeroCaret.Satisfies(SemVersion.Parse("0.14.0"))).IsFalse();
            await Assert.That(VersionRange.Parse("^0.0.3").Satisfies(SemVersion.Parse("0.0.4"))).IsFalse();

            VersionRange tilde = VersionRange.Parse("~1.2.3");
            await Assert.That(tilde.Satisfies(SemVersion.Parse("1.2.9"))).IsTrue();
            await Assert.That(tilde.Satisfies(SemVersion.Parse("1.3.0"))).IsFalse();
            await Assert.That(tilde.Satisfies(SemVersion.Parse("1.2.2"))).IsFalse();

            VersionRange xRange = VersionRange.Parse("1.2");
            await Assert.That(xRange.Satisfies(SemVersion.Parse("1.2.0"))).IsTrue();
            await Assert.That(xRange.Satisfies(SemVersion.Parse("1.2.8"))).IsTrue();
            await Assert.That(xRange.Satisfies(SemVersion.Parse("1.3.0"))).IsFalse();
            await Assert.That(VersionRange.Parse("1.x").Satisfies(SemVersion.Parse("1.9.0"))).IsTrue();
            await Assert.That(VersionRange.Parse("1.x").Satisfies(SemVersion.Parse("2.0.0"))).IsFalse();
            await Assert.That(VersionRange.Parse("1").Satisfies(SemVersion.Parse("1.9.0"))).IsTrue();

            await Assert.That(VersionRange.Parse("*").Satisfies(SemVersion.Parse("7.7.7"))).IsTrue();
            await Assert.That(VersionRange.Parse("").Satisfies(SemVersion.Parse("7.7.7"))).IsTrue();
            await Assert.That(VersionRange.Any.Satisfies(SemVersion.Parse("0.0.1"))).IsTrue();
            await Assert.That(VersionRange.Parse("").ToString()).IsEqualTo("*");
        }
    }

    [Test]
    public async Task Range_Exact_AndAlternatives()
    {
        using (Assert.Multiple())
        {
            VersionRange exact = VersionRange.Parse("1.2.3");
            await Assert.That(exact.Satisfies(SemVersion.Parse("1.2.3"))).IsTrue();
            await Assert.That(exact.Satisfies(SemVersion.Parse("1.2.4"))).IsFalse();
            await Assert.That(VersionRange.Exactly(SemVersion.Parse("1.2.3")).ToString()).IsEqualTo("1.2.3");
            await Assert.That(VersionRange.Parse("^1.0")).IsEqualTo(VersionRange.Parse("^1.0")).Because("equal when written the same");
            await Assert.That(VersionRange.Parse("^1.0")).IsNotEqualTo(VersionRange.Parse(">=1.0.0 <2.0.0"));

            VersionRange either = VersionRange.Parse("1.0.0 || >=2.0.0 <3.0.0");
            await Assert.That(either.Satisfies(SemVersion.Parse("1.0.0"))).IsTrue();
            await Assert.That(either.Satisfies(SemVersion.Parse("2.5.0"))).IsTrue();
            await Assert.That(either.Satisfies(SemVersion.Parse("1.5.0"))).IsFalse();
        }
    }

    [Test]
    public async Task Range_PrereleaseRule()
    {
        using (Assert.Multiple())
        {
            // The pin: an exact prerelease matches itself and nothing else.
            VersionRange pin = VersionRange.Parse("0.13.0-beta0001");
            await Assert.That(pin.Satisfies(SemVersion.Parse("0.13.0-beta0001"))).IsTrue();
            await Assert.That(pin.Satisfies(SemVersion.Parse("0.13.0-beta0002"))).IsFalse();
            await Assert.That(pin.Satisfies(SemVersion.Parse("0.13.0"))).IsFalse();

            // A plain range never admits a prerelease, even one inside its numeric bounds.
            await Assert.That(VersionRange.Parse("^1.0").Satisfies(SemVersion.Parse("1.5.0-rc1"))).IsFalse();
            await Assert.That(VersionRange.Parse("<2.0.0").Satisfies(SemVersion.Parse("2.0.0-rc1"))).IsFalse();
            await Assert.That(VersionRange.Parse("*").Satisfies(SemVersion.Parse("1.0.0-rc1"))).IsTrue()
                .Because("any means any; the CS2DemoKit pin is a prerelease today");
            await Assert.That(VersionRange.Parse("* || 1.0.0").Satisfies(SemVersion.Parse("1.0.0-rc1"))).IsTrue();

            // A comparator that names a prerelease admits prereleases of that same core only.
            VersionRange betas = VersionRange.Parse(">=0.13.0-beta0001 <0.14.0");
            await Assert.That(betas.Satisfies(SemVersion.Parse("0.13.0-beta0002"))).IsTrue();
            await Assert.That(betas.Satisfies(SemVersion.Parse("0.13.0"))).IsTrue();
            await Assert.That(betas.Satisfies(SemVersion.Parse("0.13.1-beta0001"))).IsFalse();
            await Assert.That(betas.Satisfies(SemVersion.Parse("0.13.0-alpha"))).IsFalse().Because("alpha sorts below beta0001");
        }
    }

    [Test]
    public async Task Range_RejectsWhatItDoesNotUnderstand()
    {
        using (Assert.Multiple())
        {
            await Assert.That(VersionRange.TryParse("latest", out _)).IsFalse();
            await Assert.That(VersionRange.TryParse(">=", out _)).IsFalse();
            await Assert.That(VersionRange.TryParse("1.2-beta", out _)).IsFalse().Because("a prerelease needs a full core");
            await Assert.That(VersionRange.TryParse(null, out _)).IsFalse();
            Assert.Throws<FormatException>(() => VersionRange.Parse("~>1.0"));
        }
    }
}
