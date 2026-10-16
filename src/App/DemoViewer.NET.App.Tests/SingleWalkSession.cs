#region

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Avalonia.Threading;
using CS2DemoKit.Analysis;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using AppSites = DemoViewer.NET.Services.DemoProcessing.DecodeSites;
using StratBookSites = DemoViewer.NET.Extensions.StratBook.Services.DecodeSites;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     One benchmark demo's visit on the real composition root, in a fresh config directory, for the
///     single-walk oracle and harness. <c>sweep</c> imports the demo with every background pass on, so the
///     queue runs one retained visit with every stamp absent. <c>open</c> imports it with every sweep off,
///     then opens it through the shell: its rules run, the scoreboard write, then the open's visit on the
///     parse in hand, with no round index on disk. <c>open-stale</c> is <c>open</c> with the round facts and
///     facts stamps cleared before the open, so the open's visit has a rules consumer to serve.
///     <c>recheck</c> imports with the sweeps on, then re-enlists the demo for the library's score repair:
///     the library row is present and every stamp current, so the library is the only pass with work.
///     Linked into the Strat Book tests.
/// </summary>
internal static class SingleWalkSession
{
    public const string Sweep = "sweep";
    public const string Open = "open";
    public const string OpenStale = "open-stale";
    public const string Recheck = "recheck";

    /// <summary>The shell's own rules run on an open. Not one of the plan's seven sites.</summary>
    public const int ShellRulesSite = 8;

    /// <summary>The engine's decode meter: one rules run's entity scan, and every caller-owned tracker that folds.</summary>
    public static readonly (string Meter, string Instrument)[] DecodeInstruments =
    [
        ("CS2DemoKit.Analysis.Evaluator", "analysis.entity_scan.started"),
        ("CS2DemoKit.Parser.Entities", "parser.entity_decode.started")
    ];

    /// <summary>
    ///     What one visit did: the decode meter's counts and the host hooks' sites that decoded, per-pass wall
    ///     time and outcome, the visit wall.
    /// </summary>
    public sealed class Visit
    {
        public required string Scenario { get; init; }
        public required string Demo { get; init; }
        public Dictionary<int, int> Sites { get; } = [];
        public Dictionary<int, int> SetupSites { get; } = [];
        public Dictionary<string, int> Meter { get; } = [];
        public Dictionary<string, int> SetupMeter { get; } = [];
        public List<(string Pass, PassOutcome Outcome, double Ms)> Passes { get; } = [];
        public double ParseMs { get; set; }
        public double WallMs { get; set; }
        public int Reads { get; set; }
        public int Decodes => Meter.Values.Sum();
        public int HookDecodes => Sites.Values.Sum();
    }

    /// <summary>Settings for a scenario's config. The library folder is the demo's own folder.</summary>
    public static string Settings(string folder, bool sweep) =>
        $$"""
          {
            "FirstRunCompleted": true,
            "Library": { "Folders": [ {{System.Text.Json.JsonSerializer.Serialize(folder)}} ] },
            "Highlights": { "BackgroundScan": {{Flag(sweep)}} },
            "Situations": { "BackgroundIndex": {{Flag(sweep)}} },
            "Grenades": { "BackgroundIndex": {{Flag(sweep)}}, "RenderLineupClips": false },
            "Playback2D": { "SuggestedTagsBackground": {{Flag(sweep)}} }
          }
          """;

    private static string Flag(bool on) => on ? "true" : "false";

    /// <summary>
    ///     Boots the composition root against <paramref name="configDir" /> (created, settings written), runs
    ///     <paramref name="body" /> on the UI thread, then disposes. The directory is left for the caller.
    /// </summary>
    public static async Task<T> WithAppAsync<T>(string configDir, string settingsJson, Func<ServiceProvider, Task<T>> body)
    {
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "settings.json"), settingsJson);
        string? prev = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, configDir);
        try
        {
            T result = default!;
            await HeadlessSession.RunOnUi(async () =>
            {
                ServiceProvider provider = App.BuildServices(new DesktopWindowService(() => null));
                try
                {
                    await SettleAsync(provider.GetRequiredService<DemoProcessingQueue>());
                    result = await body(provider);
                }
                finally
                {
                    provider.Dispose();
                }
            }, TimeSpan.FromMinutes(30));
            return result;
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prev);
        }
    }

    /// <summary>
    ///     Runs <paramref name="scenario" /> for <paramref name="demo" /> inside a booted app.
    ///     <paramref name="window" /> brackets the measured part: the sweep's import visit, or the open's visit.
    /// </summary>
    public static async Task<Visit> RunAsync(ServiceProvider provider, string scenario, string demo,
        Func<IDisposable>? window = null)
    {
        DemoProcessingQueue queue = provider.GetRequiredService<DemoProcessingQueue>();
        DemoScheduler scheduler = provider.GetRequiredService<DemoScheduler>();
        DemoLibraryService library = provider.GetRequiredService<DemoLibraryService>();
        library.FolderReader = new OnlyDemoReader(demo);
        Visit visit = new() { Scenario = scenario, Demo = demo };

        object gate = new();
        Dictionary<int, int> counting = visit.SetupSites;
        Dictionary<string, int> metering = visit.SetupMeter;
        Action<int> count = site =>
        {
            lock (gate)
            {
                counting[site] = counting.GetValueOrDefault(site) + 1;
            }
        };
        long last = 0;
        bool timing = false;
        Action<string, string, PassOutcome> finished = (path, pass, outcome) =>
        {
            if (!timing || !string.Equals(path, demo, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            lock (gate)
            {
                visit.Passes.Add((pass, outcome, Stopwatch.GetElapsedTime(last, now).TotalMilliseconds));
                last = now;
            }
        };

        using MeterListener meter = new();
        meter.InstrumentPublished = (instrument, listener) =>
        {
            if (DecodeInstruments.Contains((instrument.Meter.Name, instrument.Name)))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meter.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            lock (gate)
            {
                metering[instrument.Name] = metering.GetValueOrDefault(instrument.Name) + (int)value;
            }
        });
        meter.Start();

        AppSites.Entered = count;
        StratBookSites.Entered = count;
        scheduler.PassFinished += finished;
        try
        {
            if (scenario == Recheck)
            {
                await library.RescanAsync();
                await SettleAsync(queue);
                scheduler.RecheckAll();
                await SettleAsync(queue);

                DemoEntry entry = library.Entries.First(e => string.Equals(e.FilePath, demo, StringComparison.OrdinalIgnoreCase));
                counting = visit.Sites;
                metering = visit.Meter;
                timing = true;
                long start = Stopwatch.GetTimestamp();
                last = start;
                using (window?.Invoke())
                {
                    entry.ScoreRepairPending = true;
                    if (await library.RepairPendingScoresAsync() != 1)
                    {
                        throw new InvalidOperationException("the score repair enlisted nothing");
                    }

                    await SettleAsync(queue);
                }

                visit.WallMs = Stopwatch.GetElapsedTime(start, last).TotalMilliseconds;
            }
            else if (scenario == Sweep)
            {
                counting = visit.Sites;
                metering = visit.Meter;
                timing = true;
                long start = 0;
                using (TimeParses(queue, demo, (s, e) =>
                       {
                           start = s;
                           last = e;
                           visit.ParseMs = Stopwatch.GetElapsedTime(s, e).TotalMilliseconds;
                       }))
                using (window?.Invoke())
                {
                    await library.RescanAsync();
                    await SettleAsync(queue);
                }

                visit.WallMs = start == 0 ? 0 : Stopwatch.GetElapsedTime(start, last).TotalMilliseconds;
                // A re-check after the import must find nothing left to read.
                scheduler.RecheckAll();
                await SettleAsync(queue);
            }
            else
            {
                await library.RescanAsync();
                await SettleAsync(queue);
                scheduler.RecheckAll();
                await SettleAsync(queue);
                if (scenario == OpenStale)
                {
                    ClearRulesStamps(provider.GetRequiredService<DemoCacheStore>(), demo);
                }

                // The real open: the shell's own rules run and scoreboard write, then the open's visit.
                MainViewModel shell = provider.GetRequiredService<MainViewModel>();
                FieldInfo passesField = typeof(MainViewModel).GetField("_openPassesTask", BindingFlags.Instance | BindingFlags.NonPublic)
                                        ?? throw new MissingFieldException(nameof(MainViewModel), "_openPassesTask");
                long analysisStart = 0;
                Action<int> openCount = site =>
                {
                    if (site == ShellRulesSite)
                    {
                        Volatile.Write(ref analysisStart, Stopwatch.GetTimestamp());
                    }

                    count(site);
                };
                Action<AnalysisRun, ParsedDemo> analysed = (_, _) =>
                {
                    long now = Stopwatch.GetTimestamp();
                    lock (gate)
                    {
                        visit.Passes.Add(("shell-rules", PassOutcome.Ran, Stopwatch.GetElapsedTime(Volatile.Read(ref analysisStart), now).TotalMilliseconds));
                        last = now;
                    }
                };
                AppSites.Entered = openCount;
                StratBookSites.Entered = openCount;
                shell.Analysis.EvaluationCompleted += analysed;
                try
                {
                    counting = visit.Sites;
                    metering = visit.Meter;
                    timing = true;
                    using (TimeOpenParses(queue, (s, e) => visit.ParseMs = Stopwatch.GetElapsedTime(s, e).TotalMilliseconds))
                    using (window?.Invoke())
                    {
                        long start = Stopwatch.GetTimestamp();
                        await shell.LoadDemoFromPathAsync(demo);
                        if (passesField.GetValue(shell) is Task passes)
                        {
                            await passes;
                        }

                        visit.WallMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    }
                }
                finally
                {
                    shell.Analysis.EvaluationCompleted -= analysed;
                }

                // The shell swallows a failed load into its status line.
                if (!shell.HasFile || visit.Sites.GetValueOrDefault(ShellRulesSite) != 1)
                {
                    throw new InvalidOperationException(
                        $"the open did not run: HasFile {shell.HasFile}, shell rules runs {visit.Sites.GetValueOrDefault(ShellRulesSite)}, status '{shell.StatusText}'");
                }

                await SettleAsync(queue);
            }
        }
        finally
        {
            timing = false;
            scheduler.PassFinished -= finished;
            AppSites.Entered = null;
            StratBookSites.Entered = null;
        }

        visit.Reads = queue.ParseCount(demo);
        return visit;
    }

    // The stamps whose rows come out of a rules run: round facts and every facts ruleset.
    private static void ClearRulesStamps(DemoCacheStore cache, string demo) =>
        cache.UpdateExisting(demo, r =>
        {
            foreach (PackStamp stamp in r.PackStamps.ToList())
            {
                if (stamp.Id == RoundFactsEvaluator.EvaluatorId || stamp.Id.StartsWith(StampedFacts.StampId(""), StringComparison.Ordinal))
                {
                    r.SetStamp(stamp with { Fingerprint = null });
                }
            }
        });

    /// <summary>Reads the demo once and drops it, so every trial starts on a warm page cache.</summary>
    public static void WarmPageCache(string demo)
    {
        byte[] buffer = new byte[1 << 20];
        using FileStream stream = new(demo, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        while (stream.Read(buffer, 0, buffer.Length) > 0)
        {
        }
    }

    // Idle for a whole second: nothing queued or running, the scheduler's planning included.
    public static async Task SettleAsync(DemoProcessingQueue queue)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(20);
        int quiet = 0;
        while (quiet < 10)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the queue never went idle");
            }

            Dispatcher.UIThread.RunJobs();
            bool busy = queue.Snapshot().Any(s => s.State is DemoQueueItemState.Queued or DemoQueueItemState.Running);
            quiet = busy ? 0 : quiet + 1;
            await Task.Delay(100);
        }
    }

    // The queue's file parse, timed for one demo. Test-side: the queue has no timing seam.
    private static Restore TimeParses(DemoProcessingQueue queue, string demo, Action<long, long> parsed)
    {
        FieldInfo field = typeof(DemoProcessingQueue).GetField("_parseFile", BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new MissingFieldException(nameof(DemoProcessingQueue), "_parseFile");
        Func<string, DecodePlan, ParsedDemo> inner = (Func<string, DecodePlan, ParsedDemo>)field.GetValue(queue)!;
        field.SetValue(queue, (Func<string, DecodePlan, ParsedDemo>)((path, plan) =>
        {
            long s = Stopwatch.GetTimestamp();
            ParsedDemo result = inner(path, plan);
            if (string.Equals(path, demo, StringComparison.OrdinalIgnoreCase))
            {
                parsed(s, Stopwatch.GetTimestamp());
            }

            return result;
        }));
        return new Restore(() => field.SetValue(queue, inner));
    }

    // The open's parse of the bytes in hand. Test-side, like the file parse above.
    private static Restore TimeOpenParses(DemoProcessingQueue queue, Action<long, long> parsed)
    {
        FieldInfo field = typeof(DemoProcessingQueue).GetField("_parseBytes", BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new MissingFieldException(nameof(DemoProcessingQueue), "_parseBytes");
        Func<ReadOnlyMemory<byte>, ParsedDemo> inner = (Func<ReadOnlyMemory<byte>, ParsedDemo>)field.GetValue(queue)!;
        field.SetValue(queue, (Func<ReadOnlyMemory<byte>, ParsedDemo>)(bytes =>
        {
            long s = Stopwatch.GetTimestamp();
            ParsedDemo result = inner(bytes);
            parsed(s, Stopwatch.GetTimestamp());
            return result;
        }));
        return new Restore(() => field.SetValue(queue, inner));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    // The library folder lists this one demo, so the session indexes it and nothing else beside it.
    private sealed class OnlyDemoReader(string demo) : ILibraryFolderReader
    {
        public string ResolveRoot(string folder) => FileSystemLibraryFolderReader.Instance.ResolveRoot(folder);

        public LibraryDirectoryListing Read(string directory)
        {
            LibraryDirectoryListing all = FileSystemLibraryFolderReader.Instance.Read(directory);
            return new LibraryDirectoryListing(
                [.. all.Demos.Where(d => string.Equals(Path.GetFullPath(d.Path), Path.GetFullPath(demo), StringComparison.OrdinalIgnoreCase))],
                []);
        }
    }
}
