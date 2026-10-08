#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     Settings a first-party extension once kept in <c>settings.json</c>, copied into its own settings file the
///     first time that file is missing. It runs before anything can write <c>settings.json</c>: a write there
///     rewrites whole sections from <see cref="AppSettings" />, which no longer models these keys, so a later
///     copy would find them gone.
/// </summary>
internal static class LegacyExtensionSettings
{
    // Imported values whose file could not be written, by settings file path. The next write of settings.json
    // drops them from there, so the extension's store takes them over and saves them itself.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonObject> Unsaved =
        new(StringComparer.Ordinal);

    /// <summary>The Strat Book's id.</summary>
    public const string StratBookId = "net.demoviewer.pack.stratbook";

    // Extension key, then the settings.json path it was kept under. The keys are the Strat Book's own.
    private static readonly IReadOnlyDictionary<string, (string Key, string LegacyPath)[]> Maps =
        new Dictionary<string, (string, string)[]>(StringComparer.Ordinal)
        {
            [StratBookId] =
            [
                ("situations.backgroundIndex", "Situations:BackgroundIndex"),
                ("situations.tokenSource", "Situations:TokenSource"),
                ("grenades.backgroundIndex", "Grenades:BackgroundIndex"),
                ("grenades.trajectoryStride", "Grenades:TrajectoryStride"),
                ("grenades.renderLineupClips", "Grenades:RenderLineupClips"),
                ("grenades.lineupClipsMaxMegabytes", "Grenades:LineupClipsMaxMegabytes"),
                ("tagPalette.id", "Playback2D:TagPaletteId"),
                ("review.mode", "Playback2D:ReviewMode"),
                ("suggestedTags.background", "Playback2D:SuggestedTagsBackground")
            ]
        };

    /// <summary>
    ///     Writes each mapped extension's settings file from <paramref name="settings" /> when the file does not
    ///     exist and <c>settings.json</c> holds any of its keys. Returns how many files it wrote.
    /// </summary>
    /// <param name="settings">The app's settings file.</param>
    /// <param name="configRoot">The config root, or null (the browser), which has nothing to copy.</param>
    public static int Import(SettingsService settings, string? configRoot)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (configRoot is null)
        {
            return 0;
        }

        int written = 0;
        foreach ((string extensionId, (string Key, string LegacyPath)[] map) in Maps)
        {
            string path = ExtensionSettingsStore.PathFor(configRoot, extensionId);
            if (File.Exists(path))
            {
                continue;
            }

            JsonObject values = [];
            foreach ((string key, string legacyPath) in map)
            {
                if (Normalize(settings.ReadPersisted(legacyPath)) is { } value)
                {
                    values[key] = value;
                }
            }

            if (values.Count == 0)
            {
                continue;
            }

            try
            {
                AtomicFile.WriteAllText(path, values.ToJsonString(ExtensionSettingsStore.JsonOptions));
                written++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Unsaved[path] = values;
            }
        }

        return written;
    }

    /// <summary>The imported values for the settings file at <paramref name="path" /> that could not be written, once.</summary>
    public static JsonObject? TakeUnsaved(string path) => Unsaved.TryRemove(path, out JsonObject? values) ? values : null;

    // A hand-edited settings.json may carry "true" or "4" as strings, which the configuration binder accepted.
    private static JsonNode? Normalize(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
        {
            return node;
        }

        string text = value.GetValue<string>();
        if (bool.TryParse(text, out bool flag))
        {
            return JsonValue.Create(flag);
        }

        return long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long number)
            ? JsonValue.Create(number)
            : node;
    }
}
