#region

using Avalonia.Input;
using DemoViewer.NET.Services.Tags;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Palettes as data (tag-store.md §3.4): the built-in palette from the resource, drop-ins in filename
///     order after it, an invalid file skipped with a diagnostics line while the rest load, and the
///     validator's refusals (reserved gestures, fact and strat groups, broken flows) and warnings (a
///     shadowed tab key, named).
/// </summary>
[NotInParallel]
public class TagPaletteStoreTests
{
    private static readonly string[] BuiltInThenAlphaThenZeta = [TagPaletteStore.DefaultId, "alpha", "zeta"];
    private static readonly string[] DefaultCodes = ["A execute", "B execute", "Default", "Retake"];

    private static string Palette(string id, string hotkey = "1", string group = "outcome", string extra = "") => $$"""
        {
          "schemaVersion": 1, "id": "{{id}}", "name": "{{id}} palette",
          "panels": [
            { "id": "root", "buttons": [
                { "code": "Push", "hotkey": "{{hotkey}}", "leadSeconds": 2, "lagSeconds": 8, "then": "result" } ] },
            { "id": "result", "kind": "labels", "group": "{{group}}", "buttons": [ { "value": "won", "hotkey": "W" } ] }
          ]{{extra}}
        }
        """;

    private static TagPaletteValidation Validate(string json) =>
        TagPaletteValidator.Validate(TagPaletteStore.TryParse(json, out _)!, TagPaletteValidator.DefaultReservedGroups);

    private static string TempDir()
    {
        string dir = TagTestData.TempRoot();
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task TheDefaultPalette_LoadsFromTheResource_Clean()
    {
        TagPaletteStore store = new(null);
        TagPaletteDefinition palette = store.Default;

        using (Assert.Multiple())
        {
            await Assert.That(palette.Id).IsEqualTo(TagPaletteStore.DefaultId);
            await Assert.That(palette.Root!.Buttons.Select(b => b.Code)).IsEquivalentTo(DefaultCodes);
            await Assert.That(palette.ClampToRound).IsTrue().Because("decision D3: the shipped palette clamps");
            await Assert.That(palette.StickyGroups).Contains("opponent");
            // Refused for nothing, and shadowing exactly what overview correction 21 says it does: the
            // site "A" and outcome "L" labels are the Arrow and Line tool keys since Shape Tools, and the
            // palette's scope wins while it has focus, so the load warns and names the tool each one hides.
            await Assert.That(store.Diagnostics.Count).IsEqualTo(2)
                .Because("the shipped palette must be refused for nothing and shadow only the two tool keys");
            await Assert.That(store.Diagnostics.Any(d => d.Contains("skipped", StringComparison.Ordinal)))
                .IsFalse();
            await Assert.That(store.Diagnostics.Count(d => d.Contains(
                    "A shadows \"Arrow tool (press again for pan)\"", StringComparison.Ordinal)))
                .IsEqualTo(1);
            await Assert.That(store.Diagnostics.Count(d => d.Contains(
                    "L shadows \"Line tool (press again for pan)\"", StringComparison.Ordinal)))
                .IsEqualTo(1);
        }
    }

    [Test]
    public async Task DropIns_LoadAfterTheBuiltIn_InFilenameOrder()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "b-team.tagpalette.json"), Palette("zeta"));
            File.WriteAllText(Path.Combine(dir, "a-team.tagpalette.json"), Palette("alpha"));
            File.WriteAllText(Path.Combine(dir, "notes.json"), Palette("ignored"));

            TagPaletteStore store = new(dir);
            await Assert.That(store.Palettes.Count).IsEqualTo(1).Because("drop-ins wait for Reload, like themes");

            store.Reload();
            await Assert.That(store.Palettes.Select(p => p.Id).SequenceEqual(BuiltInThenAlphaThenZeta)).IsTrue()
                .Because("built-ins first, then drop-ins by filename, not by id");
            await Assert.That(store.Resolve("zeta").Name).IsEqualTo("zeta palette");
            await Assert.That(store.Resolve("gone").Id).IsEqualTo(TagPaletteStore.DefaultId)
                .Because("an id no palette carries falls back to the built-in");

            File.Delete(Path.Combine(dir, "b-team.tagpalette.json"));
            store.Reload();
            await Assert.That(store.Palettes.Select(p => p.Id)).DoesNotContain("zeta").Because("a reload drops deleted files");
        }
        finally
        {
            TagTestData.DeleteQuietly(dir);
        }
    }

    [Test]
    public async Task AnInvalidFile_IsSkippedWithALogLine_AndTheRestLoad()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "1-broken.tagpalette.json"), "{ \"id\": \"broken\", \"panels\": [ ");
            File.WriteAllText(Path.Combine(dir, "2-refused.tagpalette.json"), Palette("refused", "Ctrl+1"));
            File.WriteAllText(Path.Combine(dir, "3-good.tagpalette.json"), Palette("good"));
            File.WriteAllText(Path.Combine(dir, "4-builtin.tagpalette.json"), Palette(TagPaletteStore.DefaultId));

            TagPaletteStore store = new(dir);
            store.Reload();

            foreach (string line in store.Diagnostics)
            {
                Console.WriteLine($"[palette-diagnostics] {line}");
            }

            using (Assert.Multiple())
            {
                await Assert.That(store.Palettes.Select(p => p.Id)).Contains("good");
                await Assert.That(store.Palettes.Select(p => p.Id)).DoesNotContain("refused");
                await Assert.That(store.Palettes.Count(p => p.Id == TagPaletteStore.DefaultId)).IsEqualTo(1)
                    .Because("a drop-in may not take the built-in's id");
                await Assert.That(store.Diagnostics.Any(d => d.StartsWith("1-broken.tagpalette.json: skipped", StringComparison.Ordinal))).IsTrue();
                await Assert.That(store.Diagnostics.Any(d => d.StartsWith("2-refused.tagpalette.json: skipped", StringComparison.Ordinal))).IsTrue();
                await Assert.That(store.Diagnostics.Any(d => d.StartsWith("4-builtin.tagpalette.json: skipped", StringComparison.Ordinal))).IsTrue();
            }
        }
        finally
        {
            TagTestData.DeleteQuietly(dir);
        }
    }

    [Test]
    [Arguments("Ctrl+1", "app-wide")]
    [Arguments("Ctrl+P", "app-wide")]
    [Arguments("F12", "browser")]
    [Arguments("Ctrl+T", "browser")]
    [Arguments("Escape", "palette's own key")]
    [Arguments("Ctrl+M", "palette's own key")]
    [Arguments("Alt+A", "Alt or Meta")]
    public async Task AReservedGesture_IsRefused(string hotkey, string reason)
    {
        TagPaletteValidation result = Validate(Palette("p", hotkey));
        Console.WriteLine($"[palette-refused] {hotkey}: {string.Join("; ", result.Errors)}");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.Contains(reason, StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments("F", "Follow the next player")]
    [Arguments("Q", "Previous round")]
    [Arguments("Ctrl+Z", "Undo")]
    [Arguments("Y", "accept a suggested tag")]
    public async Task ATabBinding_IsShadowed_WithAWarningThatNamesIt(string hotkey, string shadowed)
    {
        TagPaletteValidation result = Validate(Palette("p", hotkey));
        Console.WriteLine($"[palette-warned] {hotkey}: {string.Join("; ", result.Warnings)}");

        await Assert.That(result.IsValid).IsTrue().Because("shadowing is the palette author's choice");
        await Assert.That(result.Warnings.Any(w => w.Contains(shadowed, StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments("buy.ct")]
    [Arguments("winner")]
    [Arguments("plantSite")]
    [Arguments("manCount.t")]
    [Arguments("strat")]
    [Arguments("strat.result")]
    public async Task AReservedGroup_IsRefused(string group)
    {
        TagPaletteValidation asPanel = Validate(Palette("p", group: group));
        TagPaletteValidation asSticky = Validate(Palette("p", extra: $", \"stickyGroups\": [\"{group}\"]"));

        await Assert.That(asPanel.IsValid).IsFalse().Because($"'{group}' is Round Facts' or the Strat Model's, never a human label");
        await Assert.That(asSticky.IsValid).IsFalse();
    }

    [Test]
    public async Task HumanGroupsRoundFactsLeavesAlone_AreAccepted()
    {
        // Correction 10: side is written as a human label; relative facts are not the refresher's.
        foreach (string group in new[] { "side", "outcome", "site", "opponent", "buy.us" })
        {
            await Assert.That(Validate(Palette("p", group: group)).IsValid).IsTrue().Because(group);
        }
    }

    [Test]
    public async Task ABrokenFlow_IsRefused()
    {
        const string loop = """
            { "id": "loop", "panels": [
                { "id": "root", "buttons": [ { "code": "X", "hotkey": "1", "then": "a" } ] },
                { "id": "a", "kind": "labels", "group": "g1", "buttons": [ { "value": "v" } ], "then": "b" },
                { "id": "b", "kind": "labels", "group": "g2", "buttons": [ { "value": "v" } ], "then": "a" } ] }
            """;
        const string missing = """
            { "id": "missing", "panels": [ { "id": "root", "buttons": [ { "code": "X", "then": "nowhere" } ] } ] }
            """;
        const string toCodes = """
            { "id": "codes", "panels": [
                { "id": "root", "buttons": [ { "code": "X", "then": "more" } ] },
                { "id": "more", "buttons": [ { "code": "Y" } ] } ] }
            """;
        const string duplicateKey = """
            { "id": "dup", "panels": [ { "id": "root", "buttons": [
                { "code": "X", "hotkey": "1" }, { "code": "Y", "hotkey": "1" } ] } ] }
            """;

        using (Assert.Multiple())
        {
            await Assert.That(Validate(loop).Errors.Any(e => e.Contains("loop", StringComparison.Ordinal))).IsTrue();
            await Assert.That(Validate(missing).Errors.Any(e => e.Contains("not a panel", StringComparison.Ordinal))).IsTrue();
            await Assert.That(Validate(toCodes).Errors.Any(e => e.Contains("codes panel", StringComparison.Ordinal))).IsTrue();
            await Assert.That(Validate(duplicateKey).Errors.Any(e => e.Contains("used twice", StringComparison.Ordinal))).IsTrue();
        }
    }

    [Test]
    public async Task UnknownFields_AreRead_NotRefused()
    {
        TagPaletteDefinition? palette = TagPaletteStore.TryParse(
            Palette("p", extra: ", \"futureRoot\": { \"kept\": true }"), out string error);

        await Assert.That(error).IsEqualTo("");
        await Assert.That(palette!.Extra!.ContainsKey("futureRoot")).IsTrue();
        await Assert.That(TagPaletteValidator.Validate(palette, TagPaletteValidator.DefaultReservedGroups).IsValid).IsTrue();
    }

    [Test]
    public async Task Hotkeys_ParseDigitsAsTheTopRow_AndMatchTheNumberPad()
    {
        await Assert.That(TagPaletteHotkey.TryParse("1", out Key one, out KeyModifiers none, out _)).IsTrue();
        await Assert.That(one).IsEqualTo(Key.D1);
        await Assert.That(none).IsEqualTo(KeyModifiers.None);
        await Assert.That(TagPaletteHotkey.Matches(one, none, Key.NumPad1, KeyModifiers.None)).IsTrue();

        await Assert.That(TagPaletteHotkey.TryParse("Ctrl+Shift+2", out Key two, out KeyModifiers chord, out _)).IsTrue();
        await Assert.That(two).IsEqualTo(Key.D2);
        await Assert.That(chord).IsEqualTo(KeyModifiers.Control | KeyModifiers.Shift);
    }
}
