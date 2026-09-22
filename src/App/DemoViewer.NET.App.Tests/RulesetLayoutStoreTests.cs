#region

using Avalonia;
using DemoViewer.NET.Modules.RuleWorkbench;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The <c>&lt;name&gt;.rules.layout.json</c> sidecar of design.md §9 decision 9: load, save, and
///     the four rules that keep it from ever costing more than an arrangement.
///     <para>
///         <b>Every one of those rules is about what happens when the sidecar is WRONG</b>, because a
///         layout store's whole risk is that it becomes a second source of truth the canvas must
///         agree with. A missing file, a stale key, a property this build does not know and a
///         half-written entry all have to resolve to "auto-layout that node", and each is a test
///         here. The store is pure file I/O over a string key, so no Avalonia app, no composition and
///         no demo is needed and the class stays in every tier.
///     </para>
/// </summary>
public class RulesetLayoutStoreTests
{
    private static readonly RulesetDocumentNodeKey _accuracy = new("aim_rating", "accuracy_pct");
    private static readonly RulesetDocumentNodeKey _attempts = new("aim_rating", "cs_attempts");
    private static readonly RulesetDocumentNodeKey _clean = new("aim_rating", "cs_clean");

    /// <summary>
    ///     A ruleset the editor has never opened has no sidecar, and that is the normal case rather
    ///     than a failure: MSAGL lays it out, and nothing had to be migrated to make that true.
    /// </summary>
    [Test]
    public async Task AMissingSidecar_IsNotAnError()
    {
        using RulesFolder folder = new();

        RulesetNodeLayout layout = RulesetLayoutStore.Load(folder.RulesetPath);

        await Assert.That(layout.Count).IsEqualTo(0);
        await Assert.That(File.Exists(folder.SidecarPath)).IsFalse()
            .Because("reading an arrangement must not create one");
    }

    /// <summary>The name decision 9 chose, spelled out: beside the ruleset, named for it.</summary>
    [Test]
    public async Task TheSidecar_SitsBesideTheRuleset_NamedForIt()
    {
        using RulesFolder folder = new();

        string sidecar = RulesetLayoutStore.SidecarPathFor(folder.RulesetPath);

        await Assert.That(Path.GetFileName(sidecar)).IsEqualTo("aim_rating.rules.layout.json");
        await Assert.That(Path.GetDirectoryName(sidecar)).IsEqualTo(folder.Root);
    }

    [Test]
    public async Task SaveThenLoad_RoundTripsEveryPosition()
    {
        using RulesFolder folder = new();
        RulesetNodeLayout arranged = RulesetNodeLayout.Empty
            .With(_accuracy, new Point(120.5, 48.25))
            .With(_attempts, new Point(-16, 0));

        await Assert.That(RulesetLayoutStore.Save(folder.RulesetPath, arranged)).IsTrue();
        RulesetNodeLayout reloaded = RulesetLayoutStore.Load(folder.RulesetPath);

        await Assert.That(reloaded.Count).IsEqualTo(2);
        await Assert.That(reloaded.PositionFor(_accuracy)).IsEqualTo(new Point(120.5, 48.25));
        await Assert.That(reloaded.PositionFor(_attempts)).IsEqualTo(new Point(-16, 0));
    }

    /// <summary>
    ///     The fallback rule, which is the whole contract between this store and the canvas: a node
    ///     the sidecar does not name keeps the position layout computed for it, and one it does name
    ///     overrides that.
    /// </summary>
    [Test]
    public async Task ANodeAbsentFromTheSidecar_KeepsItsComputedPosition()
    {
        RulesetNodeLayout layout = RulesetNodeLayout.Empty.With(_accuracy, new Point(120, 48));

        await Assert.That(layout.PositionOf(_accuracy, new Point(7, 7))).IsEqualTo(new Point(120, 48))
            .Because("an arrangement the user made wins over the computed one");
        await Assert.That(layout.PositionOf(_clean, new Point(7, 7))).IsEqualTo(new Point(7, 7));
        await Assert.That(layout.PositionFor(_clean)).IsNull();
    }

    /// <summary>
    ///     A renamed or deleted stat leaves its key behind. It must not fail the read, and it must
    ///     not take the surviving nodes with it: the sidecar outlives edits to the document.
    /// </summary>
    [Test]
    public async Task AKeyNoLongerInTheGraph_IsIgnoredRatherThanFatal()
    {
        using RulesFolder folder = new();
        folder.WriteSidecar(
            """
            {"version":1,"nodes":{
              "aim_rating.accuracy_pct":{"x":10,"y":20},
              "aim_rating.renamed_away":{"x":30,"y":40},
              "some_other_ruleset.gone":{"x":50,"y":60}}}
            """);

        RulesetNodeLayout layout = RulesetLayoutStore.Load(folder.RulesetPath);

        await Assert.That(layout.PositionFor(_accuracy)).IsEqualTo(new Point(10, 20));
        await Assert.That(layout.Count).IsEqualTo(3)
            .Because("a stale entry is kept, since a document that stops parsing must not delete an arrangement");
        await Assert.That(layout.PositionFor(_clean)).IsNull();
    }

    /// <summary>A sidecar written by a later build still places the nodes this build understands.</summary>
    [Test]
    public async Task PropertiesThisBuildDoesNotKnow_AreIgnored()
    {
        using RulesFolder folder = new();
        folder.WriteSidecar(
            """
            {"version":99,"viewport":{"zoom":1.5},"nodes":{
              "aim_rating.accuracy_pct":{"x":10,"y":20,"collapsed":true,"tint":"#ff0000"}}}
            """);

        RulesetNodeLayout layout = RulesetLayoutStore.Load(folder.RulesetPath);

        await Assert.That(layout.PositionFor(_accuracy)).IsEqualTo(new Point(10, 20));
    }

    /// <summary>
    ///     A file that cannot be parsed costs the arrangement and nothing else. It must never reach
    ///     the UI as an exception, which is the one failure mode a layout store cannot have.
    /// </summary>
    [Test]
    [Arguments("this is not json at all")]
    [Arguments("{\"version\":1,\"nodes\":{\"aim_rating.accuracy_pct\":{\"x\":10,")]
    [Arguments("[]")]
    [Arguments("{\"nodes\":\"not an object\"}")]
    [Arguments("")]
    public async Task ACorruptFile_LosesTheArrangementAndNothingElse(string contents)
    {
        using RulesFolder folder = new();
        folder.WriteSidecar(contents);

        RulesetNodeLayout layout = RulesetLayoutStore.Load(folder.RulesetPath);

        await Assert.That(layout.Count).IsEqualTo(0);
        await Assert.That(layout.PositionOf(_accuracy, new Point(7, 7))).IsEqualTo(new Point(7, 7))
            .Because("a ruleset whose sidecar is unreadable is a ruleset that gets auto-layout");
    }

    /// <summary>
    ///     Damage to one entry is contained to that entry. The alternative, failing the whole read,
    ///     turns a single hand-edited line into the loss of every position in the file.
    /// </summary>
    [Test]
    public async Task OneDamagedEntry_DoesNotCostTheOthers()
    {
        using RulesFolder folder = new();
        folder.WriteSidecar(
            """
            {"version":1,"nodes":{
              "aim_rating.accuracy_pct":{"x":10,"y":20},
              "aim_rating.no_y":{"x":30},
              "aim_rating.text_coordinate":{"x":"30","y":"40"},
              "aim_rating.not_an_object":42,
              "aim_rating.overflowed":{"x":1e400,"y":0},
              "unqualified":{"x":50,"y":60},
              "":{"x":70,"y":80},
              "aim_rating.cs_clean":{"x":90,"y":100}}}
            """);

        RulesetNodeLayout layout = RulesetLayoutStore.Load(folder.RulesetPath);

        await Assert.That(layout.PositionFor(_accuracy)).IsEqualTo(new Point(10, 20));
        await Assert.That(layout.PositionFor(_clean)).IsEqualTo(new Point(90, 100));
        await Assert.That(layout.Count).IsEqualTo(2)
            .Because("six unusable entries are skipped one at a time, not fatally");
    }

    /// <summary>
    ///     Nothing accumulates beside a ruleset that was opened and never dragged, and clearing an
    ///     arrangement removes the file rather than leaving an empty one to be read forever.
    /// </summary>
    [Test]
    public async Task AnEmptyLayout_RemovesTheSidecarRatherThanWritingOne()
    {
        using RulesFolder folder = new();
        RulesetLayoutStore.Save(folder.RulesetPath, RulesetNodeLayout.Empty.With(_accuracy, new Point(1, 2)));

        bool saved = RulesetLayoutStore.Save(folder.RulesetPath, RulesetNodeLayout.Empty);

        await Assert.That(saved).IsTrue();
        await Assert.That(File.Exists(folder.SidecarPath)).IsFalse();
        await Assert.That(RulesetLayoutStore.Load(folder.RulesetPath).Count).IsEqualTo(0);
    }

    /// <summary>
    ///     A layout holding only positions that cannot be written is the same state as an empty one,
    ///     so it leaves the same thing behind: no file.
    /// </summary>
    [Test]
    public async Task ALayoutWithNothingWritable_RemovesTheSidecarToo()
    {
        using RulesFolder folder = new();
        RulesetLayoutStore.Save(folder.RulesetPath, RulesetNodeLayout.Empty.With(_accuracy, new Point(1, 2)));

        bool saved = RulesetLayoutStore.Save(
            folder.RulesetPath, RulesetNodeLayout.Empty.With(_accuracy, new Point(double.NaN, double.NaN)));

        await Assert.That(saved).IsTrue();
        await Assert.That(File.Exists(folder.SidecarPath)).IsFalse();
    }

    /// <summary>
    ///     A shipped ruleset can sit under a directory the user cannot write. That loses the
    ///     arrangement, which is survivable, and it is reported rather than thrown.
    /// </summary>
    [Test]
    public async Task AnUnwritableLocation_ReportsFailureInsteadOfThrowing()
    {
        using RulesFolder folder = new();
        Directory.CreateDirectory(folder.SidecarPath);

        bool saved = RulesetLayoutStore.Save(
            folder.RulesetPath, RulesetNodeLayout.Empty.With(_accuracy, new Point(1, 2)));

        await Assert.That(saved).IsFalse();
        await Assert.That(RulesetLayoutStore.Load(folder.RulesetPath).Count).IsEqualTo(0)
            .Because("a sidecar that could not be written reads back as no arrangement, not as an error");
    }

    /// <summary>
    ///     A sidecar lives beside a file in version control, so one arrangement has to produce one
    ///     set of bytes however the canvas happened to enumerate its nodes.
    /// </summary>
    [Test]
    public async Task OneArrangement_SerializesToOneFile_WhateverOrderItWasBuiltIn()
    {
        RulesetNodeLayout forwards = RulesetNodeLayout.Empty
            .With(_accuracy, new Point(1, 2))
            .With(_clean, new Point(3, 4))
            .With(_attempts, new Point(5, 6));
        RulesetNodeLayout backwards = RulesetNodeLayout.Empty
            .With(_attempts, new Point(5, 6))
            .With(_clean, new Point(3, 4))
            .With(_accuracy, new Point(1, 2));

        await Assert.That(RulesetLayoutStore.Serialize(forwards))
            .IsEqualTo(RulesetLayoutStore.Serialize(backwards));
    }

    /// <summary>
    ///     Drag arithmetic produces coordinates with a tail no one needs. Rounding them keeps a
    ///     sidecar's diff to the nodes that actually moved.
    /// </summary>
    [Test]
    public async Task DragNoise_IsRoundedOutOfTheFile()
    {
        using RulesFolder folder = new();
        RulesetNodeLayout arranged = RulesetNodeLayout.Empty
            .With(_accuracy, new Point(120.00000000000001, 120.987654));

        RulesetLayoutStore.Save(folder.RulesetPath, arranged);

        await Assert.That(RulesetLayoutStore.Load(folder.RulesetPath).PositionFor(_accuracy))
            .IsEqualTo(new Point(120, 120.99));
    }

    /// <summary>
    ///     A NaN reaches a layout from a degenerate drag or a zero-size viewport, and the JSON writer
    ///     rejects one by throwing. It is dropped on the way out instead, because a node at NaN is
    ///     nowhere and auto-layout is a better answer than a failed save.
    /// </summary>
    [Test]
    public async Task ANonFiniteCoordinate_IsDroppedRatherThanFailingTheSave()
    {
        using RulesFolder folder = new();
        RulesetNodeLayout arranged = RulesetNodeLayout.Empty
            .With(_accuracy, new Point(double.NaN, 10))
            .With(_clean, new Point(20, double.PositiveInfinity))
            .With(_attempts, new Point(30, 40));

        bool saved = RulesetLayoutStore.Save(folder.RulesetPath, arranged);

        await Assert.That(saved).IsTrue();
        RulesetNodeLayout reloaded = RulesetLayoutStore.Load(folder.RulesetPath);
        await Assert.That(reloaded.Count).IsEqualTo(1);
        await Assert.That(reloaded.PositionFor(_attempts)).IsEqualTo(new Point(30, 40));
    }

    /// <summary>Forgetting a node puts it back on auto-layout, and leaves the rest arranged.</summary>
    [Test]
    public async Task Without_PutsOneNodeBackOnAutoLayout()
    {
        RulesetNodeLayout arranged = RulesetNodeLayout.Empty
            .With(_accuracy, new Point(1, 2))
            .With(_clean, new Point(3, 4));

        RulesetNodeLayout trimmed = arranged.Without(_accuracy);

        await Assert.That(trimmed.PositionFor(_accuracy)).IsNull();
        await Assert.That(trimmed.PositionFor(_clean)).IsEqualTo(new Point(3, 4));
        await Assert.That(arranged.Count).IsEqualTo(2)
            .Because("the layout is immutable, so an edit cannot reach a copy already handed out");
    }

    /// <summary>
    ///     Two rulesets can declare the same id, so the ruleset is part of the key rather than a
    ///     decoration on it, and the stored spelling has to keep them apart.
    /// </summary>
    [Test]
    public async Task TwoRulesetsSharingAnId_KeepSeparatePositions()
    {
        using RulesFolder folder = new();
        RulesetDocumentNodeKey mine = new("kast", "enemy_kills_round");
        RulesetDocumentNodeKey theirs = new("highlights_multikill", "enemy_kills_round");

        RulesetLayoutStore.Save(folder.RulesetPath, RulesetNodeLayout.Empty
            .With(mine, new Point(1, 2))
            .With(theirs, new Point(3, 4)));

        RulesetNodeLayout reloaded = RulesetLayoutStore.Load(folder.RulesetPath);

        await Assert.That(reloaded.PositionFor(mine)).IsEqualTo(new Point(1, 2));
        await Assert.That(reloaded.PositionFor(theirs)).IsEqualTo(new Point(3, 4));
    }

    /// <summary>A throwaway rules folder with one ruleset in it, so no test touches the real corpus.</summary>
    private sealed class RulesFolder : IDisposable
    {
        public RulesFolder()
        {
            Root = Path.Combine(Path.GetTempPath(), "dvn-layout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            RulesetPath = Path.Combine(Root, "aim_rating.rules.yaml");
            File.WriteAllText(RulesetPath, "ruleset: aim_rating\n");
        }

        public string Root { get; }

        public string RulesetPath { get; }

        public string SidecarPath => RulesetLayoutStore.SidecarPathFor(RulesetPath);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }

        public void WriteSidecar(string json) => File.WriteAllText(SidecarPath, json);
    }
}
