#region

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     One extension's settings file, <c>&lt;config&gt;/extension-settings/&lt;id&gt;.json</c>: a flat JSON object
///     read on first use and rewritten whole on every change. A null config root (the browser) keeps the
///     values in memory for the session. A file that does not parse reads as empty and is replaced by the
///     next write.
/// </summary>
internal sealed class ExtensionSettingsStore : IExtensionSettings
{
    /// <summary>The folder under the config root that holds one settings file per extension.</summary>
    public const string DirectoryName = "extension-settings";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _extensionId;
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Action<Action> _post;
    private Action<string>? _changed;
    private JsonObject? _values;

    /// <param name="extensionId">The extension the file belongs to.</param>
    /// <param name="configRoot">The config root, or null to keep the values in memory.</param>
    /// <param name="post">Runs a change notice on the UI thread.</param>
    public ExtensionSettingsStore(string extensionId, string? configRoot, Action<Action> post)
    {
        ArgumentException.ThrowIfNullOrEmpty(extensionId);
        ArgumentNullException.ThrowIfNull(post);
        _extensionId = extensionId;
        _path = configRoot is null ? null : PathFor(configRoot, extensionId);
        _post = post;
    }

    /// <summary>The settings file of <paramref name="extensionId" /> under <paramref name="configRoot" />.</summary>
    public static string PathFor(string configRoot, string extensionId) =>
        Path.Combine(configRoot, DirectoryName, ExtensionFolders.SafeName(extensionId) + ".json");

    private static ILogger Log => DiagnosticsLog.CreateLogger(AppLog.ExtensionsCategory);

    public event Action<string>? Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    public T Get<T>(string key, T fallback)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        JsonNode? node;
        lock (_gate)
        {
            node = Values()[key]?.DeepClone();
        }

        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.Deserialize<T>(JsonOptions) is { } value ? value : fallback;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            return fallback;
        }
    }

    public void Set<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        JsonNode? node = JsonSerializer.SerializeToNode(value, JsonOptions);
        Action<string>? changed;
        lock (_gate)
        {
            JsonObject values = Values();
            if (values.TryGetPropertyValue(key, out JsonNode? existing) && JsonNode.DeepEquals(existing, node))
            {
                return;
            }

            values[key] = node;
            Save(values);
            changed = _changed;
        }

        Raise(changed, key);
    }

    public bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Action<string>? changed;
        lock (_gate)
        {
            JsonObject values = Values();
            if (!values.Remove(key))
            {
                return false;
            }

            Save(values);
            changed = _changed;
        }

        Raise(changed, key);
        return true;
    }

    private void Raise(Action<string>? changed, string key)
    {
        if (changed is not null)
        {
            _post(() => changed(key));
        }
    }

    // Called under the lock.
    private JsonObject Values()
    {
        if (_values is not null)
        {
            return _values;
        }

        if (_path is not null && File.Exists(_path))
        {
            _values = Read(_path);
            return _values;
        }

        _values = [];
        return _values;
    }

    private JsonObject Read(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.ExtensionStoreUnreadable(Log, _extensionId, path, ex.Message);
            return [];
        }
    }

    // Called under the lock. A failed write keeps the value in memory for the session and says so in the log.
    private void Save(JsonObject values)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            AtomicFile.WriteAllText(_path, values.ToJsonString(JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.ExtensionStoreWriteFailed(Log, _extensionId, _path, ex.Message);
        }
    }
}
