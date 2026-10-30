#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.ErrorReports;

#endregion

namespace DemoViewer.NET.ViewModels.ErrorReport;

/// <summary>
///     The "Report Analysis Errors" window: the user's notes about what the analysis got wrong on one demo,
///     and the diagnostic snapshot that would let a developer reproduce it.
///     <para>
///         <b>One report per demo.</b> Opening the window loads the stored report where there is one, so the
///         user edits what they wrote last time rather than starting a second report nobody would read beside
///         the first.
///     </para>
///     <para>
///         <b>The snapshot shown is the snapshot that would be sent.</b> It is re-taken on construction and
///         again on every save, never merged: versions and fingerprints describe one run, and a half-updated
///         snapshot describes no run at all. The window shows it in full for the same reason the allowlist in
///         <see cref="ErrorReportSnapshotBuilder" /> exists, that the user is being asked to send it.
///     </para>
/// </summary>
public sealed partial class ErrorReportViewModel : ViewModelBase
{
    private readonly DemoCacheStore? _cache;
    private readonly string _demoPath;
    private readonly Func<DateTime> _now;
    private readonly Func<(ParsedDemo? Demo, string? Path)>? _openDemo;
    private readonly MergedRulesBuild? _rules;
    private DemoErrorReportSnapshot _snapshot = new();

    /// <summary>
    ///     Design-time instance for <c>Design.DataContext</c>, the capture variants and the view tests.
    ///     <para>
    ///         The seeded values are deliberately the AWKWARD ones, not pretty ones: a 64-char hash, a long
    ///         user-home path and a fingerprint are what the diagnostic rows have to lay out without clipping,
    ///         and the window's layout gates measure against them.
    ///     </para>
    /// </summary>
    public ErrorReportViewModel()
    {
        _demoPath = "";
        _now = () => DateTime.UtcNow;
        DemoFileName = "match730_pug_de_mirage_2024_11_02.dem";
        DemoFilePath =
            "/Users/you/Library/Application Support/Steam/steamapps/common/Counter-Strike Global Offensive/"
            + "game/csgo/replays/match730_pug_de_mirage_2024_11_02.dem";
        MapDisplay = "Mirage";
        IsExistingReport = true;
        CreatedText = "Created 2026-10-26 19:41";
        LastSavedText = "Last saved 2026-10-27 08:12";
        Notes =
            "Round 14 is scored as a CT win but the bomb detonated. The round-end marker looks like it lands "
            + "one tick early, so the explosion is attributed to round 15.\n\nAlso s1mple's 4k in round 9 shows "
            + "as a 3k in the scoreboard but the highlight on the overview says 4k.";
        DiagnosticGroups = DesignGroups();
    }

    /// <param name="demoPath">The demo this report is about. May not be the demo the shell has open.</param>
    /// <param name="cache">The demo cache the report is stored on.</param>
    /// <param name="rules">The merged rule set in force, for the snapshot's rule chains.</param>
    /// <param name="openDemo">The shell's open demo and its path, read only when it IS this demo.</param>
    /// <param name="now">The clock, injectable so a test can assert the saved timestamps.</param>
    public ErrorReportViewModel(
        string demoPath,
        DemoCacheStore? cache,
        MergedRulesBuild? rules,
        Func<(ParsedDemo? Demo, string? Path)> openDemo,
        Func<DateTime>? now = null)
    {
        ArgumentNullException.ThrowIfNull(openDemo);
        _demoPath = demoPath ?? "";
        _cache = cache;
        _rules = rules;
        _openDemo = openDemo;
        _now = now ?? (() => DateTime.UtcNow);

        DemoFilePath = _demoPath;
        DemoFileName = _demoPath.Length > 0 ? Path.GetFileName(_demoPath) : "(no demo)";

        DemoCacheRecord? record = _cache is not null && _demoPath.Length > 0
            ? _cache.TryLoadRecord(_demoPath, false)
            : null;
        MapDisplay = record?.Map is { Length: > 0 } map ? DemoEntry.PrettifyMap(map) : "";

        DemoErrorReport? stored = _cache is not null && _demoPath.Length > 0
            ? DemoErrorReportStore.TryLoad(_cache, _demoPath)
            : null;
        IsExistingReport = stored is not null;
        Notes = stored?.Notes ?? "";
        CreatedText = stored is { CreatedUtcTicks: > 0 } c ? "Created " + Local(c.CreatedUtcTicks) : "";
        LastSavedText = stored is { UpdatedUtcTicks: > 0 } u ? "Last saved " + Local(u.UpdatedUtcTicks) : "";

        Recapture(record);
    }

    /// <summary>The demo's file name: the report's headline identity.</summary>
    [ObservableProperty]
    private string _demoFileName = "";

    /// <summary>The demo's full path, so a report opened over a cached preview is unmistakable.</summary>
    [ObservableProperty]
    private string _demoFilePath = "";

    /// <summary>The demo's map, prettified, or empty when the header read never resolved one.</summary>
    [ObservableProperty]
    private string _mapDisplay = "";

    /// <summary>What the user is writing. The one part of this window that is theirs.</summary>
    [ObservableProperty]
    private string _notes = "";

    /// <summary>True when this window opened onto a report that already existed.</summary>
    [ObservableProperty]
    private bool _isExistingReport;

    /// <summary>When the report was first started, in words. Empty for a report that does not exist yet.</summary>
    [ObservableProperty]
    private string _createdText = "";

    /// <summary>When it was last saved, in words.</summary>
    [ObservableProperty]
    private string _lastSavedText = "";

    /// <summary>Feedback from the last save attempt. Empty until one happens.</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>The diagnostics panel's expanded state. Collapsed on open: the notes are the point.</summary>
    [ObservableProperty]
    private bool _isDiagnosticsExpanded;

    /// <summary>The snapshot as titled sections of key/value rows, exactly as it would be sent.</summary>
    [ObservableProperty]
    private IReadOnlyList<ErrorReportDiagnosticsGroup> _diagnosticGroups = [];

    /// <summary>
    ///     The whole report as plain text, notes first. Re-raised on every edit, so the copy button's promise
    ///     that it takes the notes AND the rows stays true while the user is still typing.
    /// </summary>
    public string CopyDiagnosticsText => ErrorReportSnapshotBuilder.ToText(_snapshot, Notes);

    /// <summary>
    ///     The demo this report is about. The window host compares it before replacing a live window's data
    ///     context, since a second open on the SAME demo must keep the notes the user is part-way through.
    /// </summary>
    public string DemoPath => _demoPath;

    /// <summary>Raised when the view-model wants its window closed.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    ///     Writes the notes and a freshly taken snapshot to the demo's cache record. Saving an empty report is
    ///     allowed: a user who clears their notes means to clear them.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        if (_cache is null || _demoPath.Length == 0)
        {
            StatusText = "There is no demo to attach this report to.";
            return;
        }

        try
        {
            DateTime now = _now();
            Recapture(_cache.TryLoadRecord(_demoPath, false));
            if (!DemoErrorReportStore.Save(_cache, _demoPath, Notes, _snapshot, now))
            {
                // Refused, not failed: a newer build owns this demo's report. The notes stay on screen,
                // because this window is holding the only copy of them.
                StatusText = "Not saved: a newer version of the app wrote this demo's report, and "
                             + "overwriting it is not this build's call. Copy your notes before closing.";
                return;
            }

            IsExistingReport = true;
            LastSavedText = "Last saved " + Local(now.ToUniversalTime().Ticks);

            // An empty CreatedText means there was no stored report, so this save is its creation.
            if (CreatedText.Length == 0)
            {
                CreatedText = "Created " + Local(now.ToUniversalTime().Ticks);
            }

            StatusText = "Saved.";
        }
        catch (Exception ex)
        {
            // Must say so and leave the text on screen. The notes are unrecoverable otherwise, and the copy
            // button is then the user's only way out with them.
            StatusText = "Could not save: " + ex.Message;
        }
    }

    /// <summary>
    ///     Sends the report to the developers. NOT WIRED: there is no endpoint yet, so the command can never
    ///     execute and the button stays disabled with the reason in its tooltip. It exists so the affordance
    ///     is designed and placed now, and wiring it later is a method body rather than a layout change.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSubmitForReview))]
    private void SubmitForReview()
    {
    }

    private static bool CanSubmitForReview() => false;

    /// <summary>Closes the window. Does not save: the footer's Save is the explicit act.</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    partial void OnNotesChanged(string value)
    {
        OnPropertyChanged(nameof(CopyDiagnosticsText));
        StatusText = "";
    }

    // Re-takes the snapshot and re-renders the panel. Construction and every save, never merged.
    private void Recapture(DemoCacheRecord? record)
    {
        if (_openDemo is null)
        {
            return;
        }

        (ParsedDemo? demo, string? path) = _openDemo();
        _snapshot = ErrorReportSnapshotBuilder.Build(_demoPath, record, _rules, demo, path, _now());
        DiagnosticGroups = ErrorReportSnapshotBuilder.Groups(_snapshot);
        OnPropertyChanged(nameof(CopyDiagnosticsText));
    }

    private static string Local(long utcTicks) =>
        new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    // Design-time only. See the parameterless constructor for why these values are the ugly ones.
    private static IReadOnlyList<ErrorReportDiagnosticsGroup> DesignGroups() =>
    [
        new("Demo",
        [
            new KvpRow("path",
                "/Users/you/Library/Application Support/Steam/steamapps/common/Counter-Strike Global Offensive/"
                + "game/csgo/replays/match730_pug_de_mirage_2024_11_02.dem", false, null),
            new KvpRow("sha256", "9f2c4ab1d7e85530c6b1f0a94e2d7c8351bb0f4ea9d6c23718f4b05ac9e17d6b", false, null),
            new KvpRow("size", "155398144", false, null),
            new KvpRow("demo version", "csgo-2000", false, null),
            new KvpRow("source", "GOTV", false, null),
            new KvpRow("map", "de_mirage", false, null),
            new KvpRow("server", "Valve Counter-Strike 2 Server (Stockholm)", false, null),
            new KvpRow("tick rate", "64", false, null),
            new KvpRow("duration", "2538.4", false, null),
            new KvpRow("rounds", "22", false, null)
        ]),
        new("Analysis state",
        [
            new KvpRow("cache tiers", "header 1 / parse 1 / analysis 1", false, null),
            new KvpRow("analysis state", "Indexed", false, null),
            new KvpRow("parse health", "Degraded", false, null),
            new KvpRow("parse warnings",
                "InstanceBaselineUnreadable x3 @ tick 11422: string table entry rejected", false, null)
        ]),
        new("Rules",
        [
            new KvpRow("profile (cached run)", "gotv", false, null),
            new KvpRow("fingerprint (cached run)", "cfg-7f41b2e0d9c84a16", false, null),
            new KvpRow("profile (now)", "gotv", false, null),
            new KvpRow("fingerprint (now)", "cfg-2a90de5531074bb8", false, null),
            new KvpRow("excluded rulesets", "community.entry-trades, community.utility-value", false, null),
            new KvpRow("highlight hash · multi_kill", "h-4d1e9a7c0b3f5286", false, null)
        ]),
        new("Versions",
        [
            new KvpRow("app", "0.9.1+3dd0115", false, null),
            new KvpRow("parser", "0.13.2", false, null),
            new KvpRow("analysis", "0.13.2", false, null)
        ]),
        new("Runtime",
        [
            new KvpRow("OS", "macOS 15.5 (Darwin 25.5.0)", false, null),
            new KvpRow(".NET runtime", ".NET 10.0.0", false, null),
            new KvpRow("GC mode", "Server · Interactive", false, null)
        ]),
        new("Environment",
        [
            new KvpRow("DOTNET_gcServer", "(unset)", false, null),
            new KvpRow("DEMOVIEWER_PROFILE", "(unset)", false, null),
            new KvpRow("AVALONIA_RENDER_MODE", "(unset)", false, null)
        ])
    ];
}
