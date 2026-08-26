#region

using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using DemoViewer.NET.Controls;
using CS2DemoKit.Parser;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.ViewModels.Common;
using DemoViewer.NET.ViewModels.Diagnostics;
using DemoViewer.NET.ViewModels.Parser;
using DemoViewer.NET.Views;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Headless UI tests for the unknown-net-message reverse-engineering workbench. Exercises the
///     real view models / data binding (TUnit + Avalonia headless) and captures rendered frames via
///     the Skia backend to <see cref="HeadlessSession.ArtifactDir" /> for inspection.
/// </summary>
[Category("RealDemo")]
public class UnknownMessageCardTests
{
    // ── VM-level correctness: unknowns become wire-decoded cards; selecting one shows its bytes ──
    [Test]
    public async Task SelectingFrameWithUnknowns_BuildsWireDecodedUnknownCards()
    {
        DemoCensus c = DemoCensus.Load();
        if (c.Census.Count == 0)
        {
            throw new SkipTestException("Reference demo contains no unknown net-messages.");
        }

        await HeadlessSession.RunOnUi(async () =>
        {
            ParserTabViewModel vm = NewParserTab(c);
            vm.SelectedFrame = c.Demo.Frames[c.SmallestUnknownFrame];

            List<HarvestCardViewModel> unknown = vm.MessageCards.Where(card => card.IsUnknown).ToList();
            int expected = c.Census[c.SmallestUnknownFrame].Count;

            await Assert.That(unknown.Count).IsEqualTo(expected);
            await Assert.That(unknown.Count).IsGreaterThan(0);

            HarvestCardViewModel first = unknown[0];
            await Assert.That(first.RawUnknownBytes!.Length).IsGreaterThan(0);
            await Assert.That(first.ByteSize).IsEqualTo(first.RawUnknownBytes!.Length);
            // Generic proto-wire scan produced at least one top-level field.
            await Assert.That(first.Properties.Count).IsGreaterThan(0);

            // Selecting an unknown card swaps the Frame Details hex to its exact standalone bytes.
            first.SelectCommand!.Execute(null);
            await Assert.That(vm.HexViewDecompressed.HasData).IsTrue();
            await Assert.That(vm.HexViewDecompressed.Header ?? "").Contains("UNKNOWN net-message");

            // Switching back to a known card restores the frame's Frame Details buffer (not the swap).
            HarvestCardViewModel? known = vm.MessageCards.FirstOrDefault(card => !card.IsUnknown);
            if (known is not null)
            {
                known.SelectCommand!.Execute(null);
                await Assert.That(vm.HexViewDecompressed.Header ?? "").DoesNotContain("UNKNOWN net-message");
            }
        });
    }

    // ── Render capture: the message-card list with a red "unknown" card visible ──
    [Test]
    public async Task RenderCapture_UnknownCardInCardList()
    {
        DemoCensus c = DemoCensus.Load();
        if (c.Census.Count == 0)
        {
            throw new SkipTestException("Reference demo contains no unknown net-messages.");
        }

        await HeadlessSession.RunOnUi(async () =>
        {
            ParserTabViewModel vm = NewParserTab(c);
            vm.SelectedFrame = c.Demo.Frames[c.SmallestUnknownFrame];

            InspectorCardListView view = new()
            {
                Cards = vm.MessageCards,
                HasCards = vm.MessageCards.Count > 0,
                HeaderText = $"frame {c.SmallestUnknownFrame} — {vm.MessageCards.Count} cards"
            };

            string path = Capture(view, 760, 520, "unknown_cards.png");
            Console.WriteLine($"[capture] {path}");
            await Assert.That(File.Exists(path)).IsTrue();
            await Assert.That(vm.MessageCards.Any(card => card.IsUnknown)).IsTrue();
        });
    }

    // ── Render capture: the grouped, seekable Output panel (one row per unknown type) ──
    [Test]
    public async Task RenderCapture_GroupedOutputPanel()
    {
        DemoCensus c = DemoCensus.Load();
        if (c.Census.Count == 0)
        {
            throw new SkipTestException("Reference demo contains no unknown net-messages.");
        }

        await HeadlessSession.RunOnUi(async () =>
        {
            OutputPanelViewModel output = new(new FrameNavigationViewModel())
            {
                IsVisible = true
            };
            foreach (UnknownTypeRow row in c.GroupedRows)
            {
                output.UnknownMessages.Append(new OutputRow(row.FirstFrame, row.Tick, "WARN", row.Message));
            }

            OutputPanel view = new()
            {
                DataContext = output
            };

            string path = Capture(view, 900, 320, "grouped_output.png");
            Console.WriteLine($"[capture] {path}");
            await Assert.That(File.Exists(path)).IsTrue();
            await Assert.That(output.UnknownMessages.Rows.Count).IsGreaterThan(0);
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ParserTabViewModel NewParserTab(DemoCensus c) => new(new FrameNavigationViewModel())
    {
        DemoBytesSource = () => c.DemoBytes,
        UnknownByFrame = c.Census,
        FrameListSource = () => c.Demo.Frames.ToList()
    };

    private static string Capture(Control content, int width, int height, string fileName)
    {
        Window window = new()
        {
            Width = width,
            Height = height,
            Content = content
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        string path = Path.Combine(HeadlessSession.ArtifactDir, fileName);
        window.CaptureRenderedFrame()!.Save(path);
        return path;
    }
}

/// <summary>One grouped Output-panel row for an unknown type (mirrors MainViewModel.BuildUnknownMessageCensus).</summary>
public sealed record UnknownTypeRow(int FirstFrame, string Tick, string Message);

/// <summary>
///     Parses the reference demo once and builds the unknown-message census + grouped rows, shared
///     across tests. Pure parser work — no UI thread needed.
/// </summary>
public sealed class DemoCensus
{
    private static readonly Lazy<DemoCensus> _cached = new(Build);

    private DemoCensus(ParsedDemo demo, byte[] demoBytes,
        Dictionary<int, List<UnknownMessageInfo>> census,
        int smallestUnknownFrame, List<UnknownTypeRow> groupedRows)
    {
        Demo = demo;
        DemoBytes = demoBytes;
        Census = census;
        SmallestUnknownFrame = smallestUnknownFrame;
        GroupedRows = groupedRows;
    }

    public ParsedDemo Demo { get; }
    public byte[] DemoBytes { get; }
    public Dictionary<int, List<UnknownMessageInfo>> Census { get; }

    /// <summary>Frame (with unknowns) that has the fewest inner messages — keeps the card render compact.</summary>
    public int SmallestUnknownFrame { get; }

    public List<UnknownTypeRow> GroupedRows { get; }

    public static DemoCensus Load() => _cached.Value;

    private static DemoCensus Build()
    {
        string path = ResolveDemo();
        byte[] bytes = File.ReadAllBytes(path);

        ConcurrentBag<UnknownMessageInfo> bag = new();
        Action<UnknownMessageInfo> handler = info => bag.Add(info);
        DemoParser.OnUnknownMessageType += handler;
        ParsedDemo demo;
        try
        {
            demo = DemoParser.Parse(bytes.AsMemory());
        }
        finally
        {
            DemoParser.OnUnknownMessageType -= handler;
        }

        Dictionary<int, List<UnknownMessageInfo>> census = new();
        Dictionary<int, (string Name, int First, int Size, int Count)> byType = new();
        foreach (UnknownMessageInfo info in bag)
        {
            if (!census.TryGetValue(info.FrameNumber, out List<UnknownMessageInfo>? list))
            {
                census[info.FrameNumber] = list = new List<UnknownMessageInfo>();
            }

            list.Add(info);

            if (!byType.TryGetValue(info.TypeId, out (string Name, int First, int Size, int Count) agg))
            {
                byType[info.TypeId] = (info.TypeName, info.FrameNumber, info.Length, 1);
            }
            else
            {
                byType[info.TypeId] = (agg.Name, Math.Min(agg.First, info.FrameNumber), agg.Size, agg.Count + 1);
            }
        }

        int smallest = census.Count == 0
            ? -1
            : census.Keys.OrderBy(fn => demo.Frames[fn].InnerMessages.Count).First();

        List<UnknownTypeRow> rows = byType
            .OrderByDescending(kv => kv.Value.Count)
            .Select(kv =>
            {
                int f = kv.Value.First;
                string tick = f >= 0 && f < demo.Frames.Count
                    ? (demo.Frames[f].GameTick ?? demo.Frames[f].ServerTick).ToString(CultureInfo.InvariantCulture)
                    : "—";
                string msg = $"type {kv.Key} ({kv.Value.Name})  ×{kv.Value.Count}  •  ~{kv.Value.Size} B  •  first @ frame {f}";
                return new UnknownTypeRow(f, tick, msg);
            })
            .ToList();

        return new DemoCensus(demo, bytes, census, smallest, rows);
    }

    private static string ResolveDemo()
    {
        // Prefer a checked-in pro demo (rich in unknown sound/decal events); fall back to the shared helper.
        string root = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && root.Length > 1; i++)
        {
            string candidate = Path.Combine(root, "demos", "pro-demos", "vitality-vs-fut-m1-mirage.dem");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)) ?? root;
        }

        return DemoTestHelper.RequireDemo();
    }
}
