#region

using Cs2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Library;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The bundled tour-sample pipeline below the shell: <see cref="TourDemoLocator" /> resolution (env
///     override semantics + the walk-up that finds the committed <c>assets/tour</c> asset from the test
///     bin — the "the sample actually ships in this repo" gate) and the Library VM's "Try a sample match"
///     CTA routing through the shared open funnel. The over-the-real-shell gateway targeting is covered in
///     <see cref="TutorialWalkthroughTests" />.
///     <para>
///         <c>[NotInParallel]</c>: the env-override tests mutate the process-wide
///         <c>DEMOVIEWER_TOUR_DEMO</c> variable (restored in <c>finally</c>).
///     </para>
/// </summary>
[NotInParallel]
public class TourSampleDemoTests
{
    [Test]
    public async Task Locator_EnvOverride_IsAuthoritative_IncludingAsADisableSwitch()
    {
        string tempDemo = Path.Combine(
            Path.GetTempPath(), "dvtour_" + Guid.NewGuid().ToString("N") + ".dem");
        await File.WriteAllBytesAsync(tempDemo, [1, 2, 3]);
        string? saved = Environment.GetEnvironmentVariable(TourDemoLocator.EnvVar);
        try
        {
            // An existing file wins outright…
            Environment.SetEnvironmentVariable(TourDemoLocator.EnvVar, tempDemo);
            await Assert.That(TourDemoLocator.FindSampleDemo()).IsEqualTo(tempDemo);

            // …and a set-but-unresolvable value means "no sample", NOT a walk-up fallback — that is what
            // makes the variable a disable switch (and keeps CI/dev runs pinnable).
            Environment.SetEnvironmentVariable(
                TourDemoLocator.EnvVar, Path.Combine(Path.GetTempPath(), "dvtour_does_not_exist.dem"));
            await Assert.That(TourDemoLocator.FindSampleDemo()).IsNull()
                .Because("a set env var is authoritative — no fallback past it");
        }
        finally
        {
            Environment.SetEnvironmentVariable(TourDemoLocator.EnvVar, saved);
            File.Delete(tempDemo);
        }
    }

    // The release-shaped assertion: from the test binary's own BaseDirectory, the walk-up must land on the
    // repo's committed assets/tour sample — the same resolution an installed build performs next to its exe
    // (publish.sh copies assets/ wholesale). If this fails, the sample was deleted/renamed without updating
    // the tour pipeline.
    [Test]
    public async Task Locator_WalkUp_FindsTheCommittedRepoSample()
    {
        string? saved = Environment.GetEnvironmentVariable(TourDemoLocator.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TourDemoLocator.EnvVar, null);
            string? sample = TourDemoLocator.FindSampleDemo();

            await Assert.That(sample).IsNotNull()
                .Because("the committed assets/tour sample must resolve from any bin dir under the repo");
            await Assert.That(File.Exists(sample!)).IsTrue();
            await Assert.That(Path.GetExtension(sample!)).IsEqualTo(".dem");
            await Assert.That(sample!).Contains(Path.Combine("assets", "tour"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TourDemoLocator.EnvVar, saved);
        }
    }

    // Content gate on the committed asset: it must parse, and both teams must be seated. Trimmed GOTV
    // demos structurally lack the initial player_team seating (GOTV only emits it at the halftime swap),
    // so the trimmer synthesizes those events into the output — a re-bake that loses the synthesis would
    // ship a sample where every player renders on one team, which is exactly what this pins. Cheap: the
    // trim is ~11 MiB (a sub-second parse, no entity replay).
    [Test]
    public async Task CommittedSample_Parses_WithBothTeamsSeated()
    {
        string? saved = Environment.GetEnvironmentVariable(TourDemoLocator.EnvVar);
        string? sample;
        try
        {
            Environment.SetEnvironmentVariable(TourDemoLocator.EnvVar, null);
            sample = TourDemoLocator.FindSampleDemo();
        }
        finally
        {
            Environment.SetEnvironmentVariable(TourDemoLocator.EnvVar, saved);
        }

        await Assert.That(sample).IsNotNull();
        ParsedDemo parsed = DemoParser.Parse(await File.ReadAllBytesAsync(sample!));

        await Assert.That(parsed.MapName).IsEqualTo("de_nuke");
        int t = parsed.Players.Values.Count(p => p.Team == 2);
        int ct = parsed.Players.Values.Count(p => p.Team == 3);
        using (Assert.Multiple())
        {
            await Assert.That(t).IsEqualTo(5)
                .Because("the trimmer's synthesized player_team seating must survive in the shipped sample");
            await Assert.That(ct).IsEqualTo(5);
        }
    }

    [Test]
    public async Task LibraryVm_SampleCta_RoutesThroughTheSharedOpenFunnel()
    {
        string? opened = null;
        LibraryTabViewModel vm = new(
            TestLibraries.Empty(),
            path =>
            {
                opened = path;
                return Task.CompletedTask;
            },
            () => Task.FromResult<IReadOnlyList<string>>([]),
            sampleDemoPath: "/bundle/assets/tour/sample-de_nuke.dem");

        await Assert.That(vm.HasSampleDemo).IsTrue();
        await Assert.That(vm.HasNoFolders).IsTrue().Because("an empty library shows the hero, where the CTA lives");

        await vm.OpenSampleCommand.ExecuteAsync(null);
        await Assert.That(opened).IsEqualTo("/bundle/assets/tour/sample-de_nuke.dem")
            .Because("the CTA opens the sample through the same _openDemo funnel as every other open");
    }

    [Test]
    public async Task LibraryVm_WithoutASample_HidesTheCta_AndTheCommandNoOps()
    {
        string? opened = null;
        LibraryTabViewModel vm = new(
            TestLibraries.Empty(),
            path =>
            {
                opened = path;
                return Task.CompletedTask;
            },
            () => Task.FromResult<IReadOnlyList<string>>([]));

        await Assert.That(vm.HasSampleDemo).IsFalse()
            .Because("no injected sample (Browser/WASM, designer, older tests) hides the CTA");

        await vm.OpenSampleCommand.ExecuteAsync(null);
        await Assert.That(opened).IsNull();
    }
}
