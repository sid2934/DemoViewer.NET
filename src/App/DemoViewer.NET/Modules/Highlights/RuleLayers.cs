#region

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Modules.Highlights;

/// <summary>A ruleset an extension contributed, as the host keeps it.</summary>
/// <param name="RulesetId">The qualified id the YAML must carry.</param>
/// <param name="Owner">The extension's id.</param>
/// <param name="FeatureId">The feature that switches it.</param>
/// <param name="ReadYaml">Reads the YAML; null when the extension could not supply it.</param>
public sealed record ContributedRuleset(string RulesetId, string Owner, string FeatureId, Func<string?> ReadYaml);

/// <summary>
///     The three rule layers in order: the shipped rules, then the extensions' rulesets, then the user's own
///     folder, where a same-id file overrides either. An extension ruleset is left out, with a log line, when it
///     does not load, carries another id than its qualified one, takes a shipped id, or declares a table another
///     ruleset already declares; nothing else in the set moves.
/// </summary>
public static class RuleLayers
{
    private static readonly ConditionalWeakTable<RulesetDoc, string> Sources = new();

    /// <summary>Reads the shipped folder with the user's overlay, and places the extension rulesets between them.</summary>
    /// <param name="shippedDir">The shipped rules folder.</param>
    /// <param name="userDir">The user's rules folder, or null for none.</param>
    /// <param name="extensions">The contributed rulesets.</param>
    /// <param name="log">Where a left-out ruleset is reported.</param>
    public static RuleConfigLoadResult Load(string shippedDir, string? userDir, IReadOnlyList<ContributedRuleset> extensions, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentNullException.ThrowIfNull(log);
        RuleConfigLoadResult merged = YamlConfigLoader.LoadWithOverlay(shippedDir, userDir);
        if (extensions.Count == 0)
        {
            return merged;
        }

        HashSet<string> shippedIds = new(YamlConfigLoader.TryLoadDirectory(shippedDir).Rulesets.Select(r => r.Id), StringComparer.Ordinal);
        HashSet<string> userIds = userDir is not null && Directory.Exists(userDir)
            ? new HashSet<string>(YamlConfigLoader.TryLoadDirectory(userDir).Rulesets.Select(r => r.Id), StringComparer.Ordinal)
            : [];
        return WithExtensions(merged, shippedIds, userIds, extensions, log);
    }

    /// <summary>
    ///     Places <paramref name="extensions" /> into an already merged read. <paramref name="userIds" /> are the ids
    ///     the user's folder declares, enabled or not: a user file of an extension's id wins, and one that disables
    ///     it removes it.
    /// </summary>
    public static RuleConfigLoadResult WithExtensions(RuleConfigLoadResult merged, IReadOnlySet<string> shippedIds,
        IReadOnlySet<string> userIds, IReadOnlyList<ContributedRuleset> extensions, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(merged);
        List<RulesetDoc> docs = [.. merged.Rulesets];
        HashSet<string> tables = new(docs.SelectMany(TablesOf), StringComparer.Ordinal);
        HashSet<string> taken = new(docs.Select(d => d.Id), StringComparer.Ordinal);
        foreach (ContributedRuleset contributed in extensions)
        {
            if (shippedIds.Contains(contributed.RulesetId))
            {
                RuleLayersLog.LeftOut(log, contributed.RulesetId, contributed.Owner, "it takes the id of a shipped ruleset");
                continue;
            }

            if (userIds.Contains(contributed.RulesetId))
            {
                continue;
            }

            string? yaml = contributed.ReadYaml();
            if (yaml is null)
            {
                RuleLayersLog.LeftOut(log, contributed.RulesetId, contributed.Owner, "the extension supplied no YAML");
                continue;
            }

            RuleConfigLoadResult read = YamlConfigLoader.LoadDocuments([(contributed.Owner + "/" + contributed.RulesetId + ".rules.yaml", yaml)]);
            if (!read.Success || read.Rulesets.Count != 1)
            {
                string why = read.Errors.Count > 0
                    ? string.Join("; ", read.Errors.Take(3).Select(e => e.ToString()))
                    : "it holds no ruleset, or more than one";
                string reason = "it does not load: " + why;
                RuleLayersLog.LeftOut(log, contributed.RulesetId, contributed.Owner, reason);
                continue;
            }

            RulesetDoc doc = read.Rulesets[0];
            if (!string.Equals(doc.Id, contributed.RulesetId, StringComparison.Ordinal))
            {
                string reason = $"its ruleset: key is '{doc.Id}'";
                RuleLayersLog.LeftOut(log, contributed.RulesetId, contributed.Owner, reason);
                continue;
            }

            if (taken.Contains(doc.Id))
            {
                RuleLayersLog.LeftOut(log, contributed.RulesetId, contributed.Owner, "another ruleset has its id");
                continue;
            }

            if (TablesOf(doc).FirstOrDefault(tables.Contains) is { } clash)
            {
                string reason = $"another ruleset declares its table '{clash}'";
                RuleLayersLog.LeftOut(log, contributed.RulesetId, contributed.Owner, reason);
                continue;
            }

            if (!doc.Enabled)
            {
                continue;
            }

            Sources.AddOrUpdate(doc, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(yaml))));
            docs.Add(doc);
            taken.Add(doc.Id);
            tables.UnionWith(TablesOf(doc));
        }

        return new RuleConfigLoadResult(merged.Errors, merged.LoadedFiles, merged.FailedFiles) { Rulesets = docs };
    }

    /// <summary>The SHA-256 of the YAML an extension ruleset was read from, or null for a ruleset read from a file.</summary>
    /// <param name="doc">The ruleset.</param>
    public static string? SourceOf(RulesetDoc doc) => Sources.TryGetValue(doc, out string? source) ? source : null;

    private static IEnumerable<string> TablesOf(RulesetDoc doc) => doc.Show?.Tables.Select(t => t.Name) ?? [];
}

/// <summary>The rule layers' log seams.</summary>
internal static partial class RuleLayersLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "rules: ruleset {rulesetId} of {extension} was left out: {reason}")]
    public static partial void LeftOut(ILogger logger, string rulesetId, string extension, string reason);
}
