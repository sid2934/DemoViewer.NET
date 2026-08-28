#region

using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Controls;
using DemoViewer.NET.Models;
using DemoViewer.NET.ViewModels.Common;
using FuzzySharp;

#endregion

namespace DemoViewer.NET.ViewModels.Commands;

/// <summary>
///     Command palette (Ctrl+P). One textbox aggregating: frame-jump (digits),
///     tick-jump (prefix "t"), fuzzy entity-class lookup, and ".proto" message lookup. Sources are
///     read live via callbacks so the palette always reflects the currently-loaded demo.
///     <para>
///         The design doc took an <c>IFrameNavigationService</c>; we pass the shared
///         <see cref="FrameNavigationViewModel" /> (the navigation seam) instead — see that type's
///         docs for the rationale.
///     </para>
/// </summary>
public sealed partial class CommandPaletteViewModel(
    FrameNavigationViewModel nav,
    Func<EntityTracker?> trackerSource,
    Func<ProtoIndex> protoIndexSource,
    Func<int> frameCountSource) : ObservableObject
{
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = "";

    /// <summary>Results.</summary>
    public ObservableCollection<CommandPaletteItem> Results { get; } = [];

    partial void OnIsOpenChanged(bool value)
    {
        if (!value)
        {
            Query = "";
            Results.Clear();
        }
    }

    partial void OnQueryChanged(string value) => RebuildResults(value);

    /// <summary>Opens the palette and resets the query so it always starts empty.</summary>
    [RelayCommand]
    private void Open()
    {
        Query = "";
        Results.Clear();
        IsOpen = true;
    }

    private void RebuildResults(string query)
    {
        Results.Clear();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        int frameCount = frameCountSource();

        // Frame-jump (pure digits)
        if (int.TryParse(query, out int frame) && frameCount > 0)
        {
            Results.Add(new CommandPaletteItem("›", "Go to frame " + frame, "frame",
                new RelayCommand(() =>
                {
                    nav.SeekToFrame(frame);
                    IsOpen = false;
                })));
        }

        // Tick-jump (prefix "t")
        if (query.StartsWith("t", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(query.AsSpan(1), out int tick))
        {
            Results.Add(new CommandPaletteItem("⏱", "Go to tick " + tick, "tick",
                new RelayCommand(() =>
                {
                    nav.SeekToTick(tick);
                    IsOpen = false;
                })));
        }

        // Class lookup (fuzzy)
        EntityTracker? tracker = trackerSource();
        if (tracker is not null)
        {
            foreach ((string cls, _) in tracker.AvailableClasses
                         .Distinct()
                         .Select(c => (c, score: Fuzz.PartialRatio(query, c)))
                         .Where(t => t.score >= 60)
                         .OrderByDescending(t => t.score)
                         .Take(10))
            {
                string captured = cls;
                Results.Add(new CommandPaletteItem("◇", captured, "class",
                    new RelayCommand(() =>
                    {
                        nav.RevealClass(captured);
                        IsOpen = false;
                    })));
            }
        }

        // .proto lookup
        foreach (ProtoResult proto in protoIndexSource().Search(query).Take(10))
        {
            ProtoResult captured = proto;
            Results.Add(new CommandPaletteItem("⤴", captured.MessageName, captured.RelativeFilePath,
                new RelayCommand(() =>
                {
                    OpenExternal.TryOpenInVsCode(captured.LocalPath, captured.Line);
                    IsOpen = false;
                })));
        }
    }
}

/// <summary>One palette result row. <paramref name="PickCommand" /> runs the action + closes the palette.</summary>
public sealed record CommandPaletteItem(string Icon, string Label, string Detail, ICommand PickCommand)
{
    // Result-kind flags (v0.6.0): the view maps them to Classifier* tokens via ck-* classes so the
    // glyph accents theme-resolve (was a code-held brush allocated per property get).

    /// <summary>Frame result (blue glyph).</summary>
    public bool IsKindFrame => Detail == "frame";

    /// <summary>Tick result (green glyph).</summary>
    public bool IsKindTick => Detail == "tick";

    /// <summary>Entity-class result (purple glyph).</summary>
    public bool IsKindClass => Detail == "class";

    /// <summary>.proto path result — the fallback kind (orange glyph).</summary>
    public bool IsKindProto => !IsKindFrame && !IsKindTick && !IsKindClass;
}
