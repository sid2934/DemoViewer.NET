#region

using System.Globalization;
using System.Reflection;
using Avalonia;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser;
using DemoViewer.NET.Diagnostics;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.ViewModels.Diagnostics;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Services.ErrorReports;

/// <summary>
///     Builds the diagnostic snapshot that travels with an error report: everything a developer needs to
///     reproduce what the user is complaining about.
///     <para>
///         <b>Everything is collected by name.</b> No settings object, no environment, no ruleset body is
///         serialised wholesale. Environment variables come only from
///         <see cref="RuntimeEnvInfo.EnvAllowlist" />, which exists because enumerating all of them leaks
///         Steam tokens and auth. This payload is built to leave the machine.
///     </para>
///     <para>
///         <b>Nothing here may throw.</b> A snapshot is a best-effort description of a broken state, often
///         taken while something is already wrong; a collector that threw would cost the user the notes they
///         had just written. Every section is guarded individually, so one failure costs one section.
///     </para>
/// </summary>
public static class ErrorReportSnapshotBuilder
{
    /// <summary>
    ///     Takes a snapshot for the demo at <paramref name="demoPath" />.
    /// </summary>
    /// <param name="demoPath">The demo the report is about. May be a demo that is not the one open.</param>
    /// <param name="record">That demo's cache record, or null when it is not cached.</param>
    /// <param name="rules">The merged rule set in force, or null when it cannot be resolved.</param>
    /// <param name="openDemo">The demo the shell has open, or null. Only read when it IS <paramref name="demoPath" />.</param>
    /// <param name="openDemoPath">The path of <paramref name="openDemo" />, for that comparison.</param>
    /// <param name="nowUtc">The capture time.</param>
    public static DemoErrorReportSnapshot Build(
        string demoPath,
        DemoCacheRecord? record,
        MergedRulesBuild? rules,
        ParsedDemo? openDemo,
        string? openDemoPath,
        DateTime nowUtc)
    {
        DemoErrorReportSnapshot snapshot = new()
        {
            CapturedAtUtcTicks = nowUtc.ToUniversalTime().Ticks,
            DemoPath = demoPath
        };

        AddDemo(snapshot, record);
        AddCacheState(snapshot, record);
        AddRules(snapshot, rules, record);
        AddParseHealth(snapshot, demoPath, openDemo, openDemoPath);
        AddVersions(snapshot);
        AddPacks(snapshot);
        AddRuntime(snapshot);
        return snapshot;
    }

    /// <summary>
    ///     The snapshot rendered as plain text, which is what the window's copy button hands over and what a
    ///     future submission would carry alongside the notes.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="notes">The user's notes, written first because they are the point.</param>
    public static string ToText(DemoErrorReportSnapshot snapshot, string notes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        System.Text.StringBuilder sb = new();
        sb.AppendLine("ANALYSIS ERROR REPORT");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(notes) ? "(no notes written)" : notes.Trim());
        sb.AppendLine();
        foreach (ErrorReportDiagnosticsGroup group in Groups(snapshot))
        {
            sb.AppendLine(group.Title.ToUpperInvariant());
            foreach (KvpRow row in group.Rows)
            {
                sb.Append("  ").Append(row.Key).Append(": ").AppendLine(row.Value);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>The snapshot as the window shows it: one group per section, each a list of key/value rows.</summary>
    /// <param name="snapshot">The snapshot.</param>
    public static IReadOnlyList<ErrorReportDiagnosticsGroup> Groups(DemoErrorReportSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<ErrorReportDiagnosticsGroup> groups =
        [
            new("Demo",
            [
                Row("path", snapshot.DemoPath),
                Row("sha256", snapshot.DemoSha256 ?? "(not hashed)"),
                Row("size", snapshot.DemoSize.ToString(CultureInfo.InvariantCulture)),
                Row("demo version", snapshot.DemoVersion ?? "(unknown)"),
                Row("source", snapshot.SourceKind ?? "(unknown)"),
                Row("map", snapshot.Map ?? "(unknown)"),
                Row("server", snapshot.Server ?? "(unknown)"),
                Row("tick rate", snapshot.TickRate.ToString(CultureInfo.InvariantCulture)),
                Row("duration", snapshot.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)),
                Row("rounds", snapshot.RoundCount.ToString(CultureInfo.InvariantCulture))
            ]),
            new("Analysis state",
            [
                Row("cache tiers", snapshot.CacheTiers ?? "(not cached)"),
                Row("analysis state", snapshot.AnalysisState ?? "(unknown)"),
                Row("parse health", snapshot.ParseHealth ?? "(demo not open)"),
                Row("parse warnings", snapshot.ParseWarnings.Count == 0
                    ? "none"
                    : string.Join(" | ", snapshot.ParseWarnings))
            ]),
            new("Rules", RulesRows(snapshot))
        ];

        if (snapshot.Rulesets.Count > 0)
        {
            groups.Add(new ErrorReportDiagnosticsGroup("Rule chains",
            [
                .. snapshot.Rulesets.Select(r => Row(
                    r.Enabled ? r.Id : r.Id + " (off)",
                    string.Join(" · ",
                        new[]
                        {
                            r.Title(), r.CatalogVersion is { Length: > 0 } cv ? "catalog " + cv : null,
                            r.Hash is { Length: > 0 } h ? h : null, r.Origin
                        }.Where(x => !string.IsNullOrEmpty(x))!)))
            ]));
        }

        groups.Add(new ErrorReportDiagnosticsGroup("Versions",
            [.. snapshot.Versions.Select(kv => Row(kv.Key, kv.Value))]));
        if (snapshot.Packs.Count > 0)
        {
            groups.Add(new ErrorReportDiagnosticsGroup("Extension packs",
                [.. snapshot.Packs.Select((p, i) => Row(
                    i.ToString(CultureInfo.InvariantCulture), p))]));
        }

        groups.Add(new ErrorReportDiagnosticsGroup("Runtime",
            [.. snapshot.Runtime.Select(kv => Row(kv.Key, kv.Value))]));
        groups.Add(new ErrorReportDiagnosticsGroup("Environment",
            [.. snapshot.Environment.Select(kv => Row(kv.Key, kv.Value))]));
        return groups;
    }

    private static List<KvpRow> RulesRows(DemoErrorReportSnapshot snapshot)
    {
        List<KvpRow> rows =
        [
            Row("profile (cached run)", snapshot.CachedProfileName ?? "(none)"),
            Row("fingerprint (cached run)", snapshot.CachedConfigFingerprint ?? "(none)"),
            Row("profile (now)", snapshot.CurrentProfileName ?? "(none)"),
            Row("fingerprint (now)", snapshot.CurrentConfigFingerprint ?? "(none)")
        ];

        // Named rather than counted: an excluded ruleset contributes nothing and reads exactly like rules that
        // ran and matched nothing, which is the most likely thing behind an "analysis error" report. The
        // unknown case is spelled out, because an empty list that means "we never looked" would send a
        // developer hunting elsewhere.
        rows.Add(Row("excluded rulesets", !snapshot.ExcludedRulesetsKnown
            ? "(unknown: nothing composed in this session)"
            : snapshot.ExcludedRulesets.Count == 0
                ? "none"
                : string.Join(", ", snapshot.ExcludedRulesets)));
        foreach ((string id, string hash) in snapshot.HighlightHashes.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            rows.Add(Row("highlight hash · " + id, hash));
        }

        return rows;
    }

    private static void AddDemo(DemoErrorReportSnapshot snapshot, DemoCacheRecord? record)
    {
        if (record is null)
        {
            return;
        }

        try
        {
            snapshot.DemoSha256 = record.Sha256;
            snapshot.DemoSize = record.Size;
            snapshot.DemoVersion = record.DemoVersion;
            snapshot.SourceKind = record.SourceKind;
            snapshot.Map = record.Map;
            snapshot.Server = record.Server;
            snapshot.TickRate = record.TickRate;
            snapshot.DurationSeconds = record.DurationSeconds;
            snapshot.RoundCount = record.Rounds.Count > 0 ? record.Rounds.Count : record.RoundCount;
        }
        catch (Exception)
        {
            // One missing section beats losing the report.
        }
    }

    private static void AddCacheState(DemoErrorReportSnapshot snapshot, DemoCacheRecord? record)
    {
        if (record is null)
        {
            return;
        }

        try
        {
            snapshot.CacheTiers = string.Create(CultureInfo.InvariantCulture,
                $"header {record.Header.Schema} / parse {record.Parse.Schema} / analysis {record.Analysis.Schema}");
            snapshot.AnalysisState = record.AnalysisState.ToString();
            snapshot.CachedProfileName = record.ProfileName;
            snapshot.CachedConfigFingerprint = record.ConfigFingerprint;
            foreach ((string id, string hash) in record.HighlightHashes)
            {
                snapshot.HighlightHashes[id] = hash;
            }
        }
        catch (Exception)
        {
        }
    }

    private static void AddRules(DemoErrorReportSnapshot snapshot, MergedRulesBuild? rules, DemoCacheRecord? record)
    {
        if (rules is null)
        {
            return;
        }

        // The tick rate the identities are computed at. A ruleset's identity is per tick rate, so a snapshot
        // taken at the wrong one would name hashes this demo's outputs were never stamped with.
        int tickRate = record is { TickRate: > 0 } ? record.TickRate : 64;
        IReadOnlyList<RulesetDoc> docs;
        try
        {
            docs = rules.Docs;
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            snapshot.CurrentConfigFingerprint = rules.Fingerprint(tickRate).Fingerprint;
            snapshot.CurrentProfileName = RulesHighlightHarvester.GotvProfileId;
        }
        catch (Exception)
        {
            // A set that does not compose has no fingerprint, which is itself worth seeing in the report.
            snapshot.CurrentConfigFingerprint = "(does not compose)";
        }

        // The real list comes from a composed BuildResult, which this cannot build: composing the graph is
        // seconds of work and this runs on a button press. RulesetExclusionReport is the single funnel every
        // DemoAnalysis.Build call site already goes through, so it remembers the last set for us.
        if (RulesetExclusionReport.LastExcluded is { } excluded)
        {
            snapshot.ExcludedRulesetsKnown = true;
            snapshot.ExcludedRulesets.AddRange(excluded);
        }

        foreach (RulesetDoc doc in docs)
        {
            string? hash = null;
            try
            {
                hash = rules.RulesetIdentity(doc.Id, tickRate);
            }
            catch (Exception)
            {
                // Throws when the ruleset is disabled or its closure does not compose; both show as no hash.
            }

            snapshot.Rulesets.Add(new DemoErrorReportRuleset
            {
                Id = doc.Id,
                Name = doc.Title,
                Origin = OriginOf(doc),
                Hash = hash,
                CatalogVersion = doc.CatalogVersion,
                Enabled = doc.Enabled
            });
        }
    }

    // The file a ruleset was read from, by NAME only. The full path is the user's home directory and has no
    // bearing on reproducing anything.
    private static string? OriginOf(RulesetDoc doc)
    {
        try
        {
            return doc.Position.File is { Length: > 0 } file ? Path.GetFileName(file) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void AddParseHealth(DemoErrorReportSnapshot snapshot, string demoPath, ParsedDemo? openDemo,
        string? openDemoPath)
    {
        // Only when the report is about the demo actually open: health belongs to a parse, and a cached
        // preview of another demo has none of its own.
        if (openDemo is null || openDemoPath is null
            || !string.Equals(demoPath, openDemoPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            snapshot.ParseHealth = openDemo.Health.ToString();
            foreach (ParseWarning warning in openDemo.Warnings)
            {
                // Code and count first: a developer triages on which warning and how many, and the message
                // is the same sentence every time for a given code.
                snapshot.ParseWarnings.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{warning.Code} x{warning.Count} @ tick {warning.Tick}: {warning.Message}"));
            }
        }
        catch (Exception)
        {
        }
    }

    private static void AddVersions(DemoErrorReportSnapshot snapshot)
    {
        Add("app", typeof(ErrorReportSnapshotBuilder).Assembly);
        Add("parser", typeof(DemoParser).Assembly);
        Add("analysis", typeof(RulesetDoc).Assembly);
        Add("avalonia", typeof(Application).Assembly);
        Add("skiasharp", typeof(SKBitmap).Assembly);

        void Add(string key, Assembly asm)
        {
            try
            {
                snapshot.Versions[key] = InformationalVersion(asm);
            }
            catch (Exception)
            {
            }
        }
    }

    private static void AddPacks(DemoErrorReportSnapshot snapshot)
    {
        try
        {
            foreach (PackStatus status in FeaturePacks.Statuses)
            {
                string version = InformationalVersion(status.Pack.GetType().Assembly);
                string state = status.IsCompatible ? "ok" : status.Problem ?? "incompatible";
                snapshot.Packs.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{status.Pack.Id} {version} · {status.Source} · {state}"));
            }
        }
        catch (Exception)
        {
        }
    }

    private static void AddRuntime(DemoErrorReportSnapshot snapshot)
    {
        try
        {
            foreach (KvpRow row in RuntimeEnvInfo.SystemRows())
            {
                snapshot.Runtime[row.Key] = row.Value;
            }
        }
        catch (Exception)
        {
        }

        try
        {
            // RuntimeEnvInfo's allowlist, never the whole environment. See the type comment.
            foreach (KvpRow row in RuntimeEnvInfo.EnvRows())
            {
                snapshot.Environment[row.Key] = row.Value;
            }
        }
        catch (Exception)
        {
        }
    }

    private static string InformationalVersion(Assembly asm) =>
        asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? asm.GetName().Version?.ToString()
        ?? "(unknown)";

    private static KvpRow Row(string key, string value) => new(key, value, false, null);
}

/// <summary>One titled section of a report's diagnostics, as the window lists them.</summary>
/// <param name="Title">The section heading.</param>
/// <param name="Rows">Its key/value rows.</param>
public sealed record ErrorReportDiagnosticsGroup(string Title, IReadOnlyList<KvpRow> Rows);

internal static class RulesetRowExtensions
{
    internal static string? Title(this DemoErrorReportRuleset row) =>
        string.IsNullOrEmpty(row?.Name) ? null : row.Name;
}
