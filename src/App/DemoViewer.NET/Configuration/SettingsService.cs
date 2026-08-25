#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DemoViewer.NET.Models;
using DemoViewer.NET.Services;
using Microsoft.Extensions.Configuration;

#endregion

namespace DemoViewer.NET.Configuration;

/// <summary>
///     The writable layer over the read-only Microsoft configuration stack for the app's
///     <c>settings.json</c>. It owns a live <see cref="IConfigurationRoot" /> (JSON file +
///     <c>DEMOVIEWER_</c>-prefixed environment variables) and adds an atomic, self-reloading
///     <see cref="Write" /> so a mutation is durable and immediately visible to every
///     <c>IOptionsMonitor&lt;AppSettings&gt;</c> bound to <see cref="Configuration" />.
///     <para>
///         <b>OnChange threading.</b> A <see cref="Write" /> reloads synchronously on the calling thread,
///         so self-writes raise <c>IOptionsMonitor.OnChange</c> deterministically inline — no marshaling on
///         that path. The <c>reloadOnChange</c> file watcher additionally fires for <em>external</em> edits
///         on a threadpool thread; consumers that touch UI state from OnChange MUST marshal to
///         <c>Dispatcher.UIThread</c> themselves.
///     </para>
///     <para>
///         Robust to a corrupt or partial file: a JSON load failure is swallowed (defaults are kept) and
///         surfaced through <see cref="NeedsFirstRun" /> rather than thrown.
///     </para>
/// </summary>
public sealed class SettingsService
{
    private const string SettingsFileName = "settings.json";
    private const string EnvPrefix = "DEMOVIEWER_";

    // Reused per CA1869 — constructing JsonSerializerOptions per call is expensive.
    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    // In-memory provider backing the WASM / no-filesystem path; null on desktop. A resettable variant
    // (see the nested type) so a write can atomically REPLACE the whole key set — the built-in
    // MemoryConfigurationProvider has no clear, which would leak stale keys on a shrinking collection.
    private readonly ResettableMemoryConfigurationProvider? _mem;

    private readonly string? _settingsPath;

    // Set by the file-load exception handler when settings.json is present but unparseable.
    private bool _loadFailed;

    /// <summary>Constructs over the real app-data config root (<see cref="AppPaths.ConfigRoot" />).</summary>
    public SettingsService() : this(AppPaths.ConfigRoot)
    {
    }

    /// <summary>
    ///     Constructs over an explicit config directory. This is the test seam — pass a temp dir to keep a
    ///     test run out of the real user config folder. A <c>null</c> directory (WASM) selects the
    ///     in-memory, file-less path.
    /// </summary>
    public SettingsService(string? configDir)
    {
        bool fileless = OperatingSystem.IsBrowser() || configDir is null;
        _settingsPath = fileless ? null : Path.Combine(configDir!, SettingsFileName);

        ConfigurationBuilder builder = new();
        if (fileless)
        {
            // No filesystem to watch: an in-memory provider instead. Reload()/OnChange still work for
            // programmatic writes because the provider's Load() is a no-op (its Data survives Reload), so a
            // mutate-then-Reload fires the change token just like the file source. The resettable variant
            // is added directly (it is its own IConfigurationSource) so a write can atomically replace all
            // keys — see WriteInMemory.
            _mem = new ResettableMemoryConfigurationProvider();
            builder.Add(_mem);
        }
        else
        {
            Directory.CreateDirectory(configDir!); // ensure the base path exists before the watcher attaches
            builder.SetBasePath(configDir!)
                .AddJsonFile(SettingsFileName, true, true);
            // Swallow a corrupt/partial file — keep defaults, flag first-run — instead of throwing at Build.
            builder.SetFileLoadExceptionHandler(ctx =>
            {
                _loadFailed = true;
                ctx.Ignore = true;
            });
        }

        builder.AddEnvironmentVariables(EnvPrefix);

        IConfigurationRoot root = builder.Build();
        Configuration = root;
    }

    /// <summary>
    ///     The live configuration root. Exposed as <see cref="IConfiguration" /> so the DI bootstrap can
    ///     <c>services.Configure&lt;AppSettings&gt;(svc.Configuration)</c>; a <see cref="Write" /> reloads it
    ///     in place so bound options monitors stay current.
    /// </summary>
    public IConfiguration Configuration { get; }

    /// <summary>
    ///     The current settings, bound on demand from <see cref="Configuration" />. Always non-null: binding
    ///     starts from a defaulted <see cref="AppSettings" />, so a missing/partial file yields defaults.
    /// </summary>
    public AppSettings Current => Configuration.Get<AppSettings>() ?? new AppSettings();

    /// <summary>
    ///     <c>true</c> when no usable settings file has been persisted yet — the file is absent, or present
    ///     but failed to parse. Drives the first-run experience.
    /// </summary>
    // Based on the AppSettings.FirstRunCompleted flag (set only by the wizard's Finish/Skip), NOT on whether
    // settings.json exists — the demo-library folder migration can create the file as a side effect during
    // BuildServices, which must NOT count as "setup done" (an upgrading user has still never picked a
    // category). A missing/corrupt file binds to defaults (FirstRunCompleted == false), so this is naturally
    // true for a fresh or unparseable config.
    // WASM: no persisted file, so FirstRunCompleted resets to false each page load and this is ALWAYS true —
    // the wizard is therefore auto-shown on the DESKTOP host only (App.axaml.cs guards to the classic-desktop
    // lifetime); on WASM it is reachable solely via Settings' "Re-run first-time setup", never auto-shown, so
    // it cannot loop every page load. WASM users get the PowerUser defaults out of the box.
    public bool NeedsFirstRun => _loadFailed || !Current.FirstRunCompleted;

    /// <summary>
    ///     Mutates the current settings and persists them, then reloads <see cref="Configuration" />
    ///     synchronously. On desktop the write is atomic (temp file + <c>File.Move</c> overwrite) so a
    ///     watcher never observes a half-written file. Writing the same values twice is harmless.
    ///     <para>
    ///         The reload fires the change token inline on the calling thread, so any
    ///         <c>IOptionsMonitor&lt;AppSettings&gt;</c> over this configuration raises <c>OnChange</c>
    ///         synchronously before this method returns.
    ///     </para>
    /// </summary>
    public void Write(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        // The write BASIS is the persisted (file / in-memory) state, NOT the env-layered Current: a
        // transient DEMOVIEWER_-prefixed override is the highest-precedence layer in Current, so using it
        // as the basis would bake that override into settings.json on an unrelated write and permanently
        // overwrite the user's real file value. Env overrides stay effective for READS (Current) — they
        // are simply never persisted by a write.
        AppSettings settings = ReadPersistedBasis();
        mutate(settings);

        if (_settingsPath is null)
        {
            WriteInMemory(settings);
        }
        else
        {
            // Merge the mutated PREFERENCE keys into the existing file object, leaving the consolidated
            // Session / Recents sections (which AppSettings does not model) untouched — a preference write
            // must never clobber them.
            JsonObject file = ReadFileObject();
            JsonObject prefs = JsonSerializer.SerializeToNode(settings, _serializerOptions)!.AsObject();
            foreach (string key in prefs.Select(kv => kv.Key).ToList())
            {
                file[key] = prefs[key]?.DeepClone();
            }

            WriteObject(file);
            _loadFailed = false; // we just wrote valid JSON — any prior parse failure is cleared
        }

        // Reload immediately: fires the reload token synchronously on this thread, so self-write OnChange is
        // deterministic. The file watcher (desktop) will additionally re-fire for the same change on a
        // threadpool thread — with identical values, so it is a harmless no-op refresh.
        ((IConfigurationRoot)Configuration).Reload();
    }

    /// <summary>
    ///     Loads the persisted UI session-restore snapshot (the <c>Session</c> section of the single
    ///     config file), or <c>null</c> when none. On first load after upgrade, one-time-imports a legacy
    ///     <c>session.json</c> from the same dir when the section is absent (see <see cref="MigrateLegacySection" />).
    ///     No env layer (session is never env-overridden). WASM/fileless → <c>null</c>.
    /// </summary>
    public SessionPayload? LoadSession()
    {
        SessionPayload? current = ReadSection<SessionPayload>("Session");
        if (current is not null || _settingsPath is null)
        {
            return current;
        }

        // Section absent → import a legacy session.json (once), persisting it into the merged file and
        // renaming the old file to .bak. Guarded so a re-import can't happen (after import the section is
        // non-null; the .bak rename also removes the source).
        return MigrateLegacySection<SessionPayload>("session.json", imported => SaveSession(imported));
    }

    /// <summary>
    ///     Persists the UI session-restore snapshot into the single config file's <c>Session</c> section,
    ///     preserving every other section (single-serializer, no clobber). Deliberately does NOT
    ///     <c>Reload()</c> — session state is not read through <c>IOptionsMonitor</c>, so a self-write raises
    ///     no synchronous <c>OnChange</c> and never thrashes the feature gate (churn control). No-op on
    ///     WASM/fileless.
    /// </summary>
    public void SaveSession(SessionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (_settingsPath is null)
        {
            return; // WASM/fileless: session does not persist (matches the pre-consolidation behavior)
        }

        try
        {
            WriteSection("Session", payload);
        }
        catch
        {
            // Best-effort, like the pre-consolidation SessionStore: a disk-full / locked-file / permission
            // failure must never crash the app (this runs from the shutdown handler). The reactive preference
            // Write path keeps rethrowing — only these non-preference sections swallow.
        }
    }

    /// <summary>
    ///     Loads the persisted recents list (the <c>Recents</c> section), most-recent-first, or an
    ///     empty list when none. One-time-imports a legacy <c>recent-files.json</c> when the section is absent.
    ///     WASM/fileless → empty.
    /// </summary>
    public IReadOnlyList<RecentFile> LoadRecents()
    {
        List<RecentFile>? current = ReadSection<List<RecentFile>>("Recents");
        if (current is not null || _settingsPath is null)
        {
            return current ?? [];
        }

        return MigrateLegacySection<List<RecentFile>>("recent-files.json", imported => SaveRecents(imported)) ?? [];
    }

    /// <summary>
    ///     Persists the recents list into the single config file's <c>Recents</c> section, preserving every
    ///     other section. Like <see cref="SaveSession" /> it does NOT <c>Reload()</c> (recents are not read
    ///     through <c>IOptionsMonitor</c>). No-op on WASM/fileless.
    /// </summary>
    public void SaveRecents(IReadOnlyList<RecentFile> recents)
    {
        ArgumentNullException.ThrowIfNull(recents);
        if (_settingsPath is null)
        {
            return;
        }

        try
        {
            WriteSection("Recents", recents.ToList());
        }
        catch
        {
            // Best-effort, like the pre-consolidation RecentFilesStore: a write failure during a demo-open
            // must never crash the app. The reactive preference Write path keeps rethrowing.
        }
    }

    // One-time import of a legacy per-user store that has since been folded into the single config file.
    // Reads <configDir>/<legacyFileName>; if present and parseable, hands it to <persist> (which writes it
    // into the merged file) and renames the source to <name>.bak so the user can restore. Idempotent: once
    // the section is persisted the caller's absent-section guard never re-enters; the .bak rename also
    // removes the source. Best-effort — any I/O or parse failure returns default and leaves the source in
    // place. NOTE: reads <configDir>-relative (not AppPaths) so the test config-dir override is honored; a
    // macOS install whose legacy file still sits in the pre-unification ~/.config is not found (accepted
    // minor loss — session/recents are ephemeral).
    private T? MigrateLegacySection<T>(string legacyFileName, Action<T> persist) where T : class
    {
        string legacyPath = Path.Combine(Path.GetDirectoryName(_settingsPath!)!, legacyFileName);
        if (!File.Exists(legacyPath))
        {
            return null;
        }

        try
        {
            T? imported = JsonSerializer.Deserialize<T>(File.ReadAllText(legacyPath), _serializerOptions);
            if (imported is not null)
            {
                persist(imported); // fold into the single config file
            }

            RenameToBak(legacyPath); // preserve, never delete
            return imported;
        }
        catch
        {
            return null; // best-effort migration; a corrupt/locked legacy file is left untouched
        }
    }

    private static void RenameToBak(string path)
    {
        try
        {
            string bak = path + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }

            File.Move(path, bak);
        }
        catch
        {
            // Best-effort: a locked source just stays in place (the absent-section guard still prevents a
            // re-import once the merged section is written).
        }
    }

    // Reads a non-preference section (Session / Recents) as a strongly-typed value via System.Text.Json
    // (which handles the positional records the configuration binder cannot). Absent / null / unparseable
    // section → default. Never touches the env layer (these sections are never env-overridden).
    private T? ReadSection<T>(string sectionName) where T : class
    {
        if (_settingsPath is null)
        {
            return null; // WASM/fileless: these sections do not persist
        }

        if (ReadFileObject().TryGetPropertyValue(sectionName, out JsonNode? node) && node is not null)
        {
            try
            {
                return node.Deserialize<T>(_serializerOptions);
            }
            catch
            {
                return null; // a corrupt section is best-effort — treat as absent
            }
        }

        return null;
    }

    // Sets one non-preference section on the file object and writes it back, PRESERVING every other section
    // (preferences + the sibling Session/Recents). Deliberately does NOT Reload() — see SaveSession/SaveRecents.
    private void WriteSection<T>(string sectionName, T value)
    {
        JsonObject file = ReadFileObject();
        file[sectionName] = JsonSerializer.SerializeToNode(value, _serializerOptions);
        WriteObject(file);
    }

    // The whole config file as a mutable JSON object (or an empty one when absent / unparseable). This is the
    // basis every write merges into so no section clobbers another — AppSettings does not model Session /
    // Recents, so a whole-AppSettings serialize would drop them; a node merge keeps them.
    private JsonObject ReadFileObject()
    {
        if (_settingsPath is null || !File.Exists(_settingsPath))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(_settingsPath)) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject(); // a corrupt file is repaired by the next write
        }
    }

    // Atomically swaps the given object into place (temp + File.Move overwrite), so a watcher never observes a
    // half-written file. Used by the reactive preference Write (which additionally Reloads) and the
    // non-reactive Session/Recents saves (which do not).
    private void WriteObject(JsonObject file)
    {
        string json = file.ToJsonString(_serializerOptions);
        string dir = Path.GetDirectoryName(_settingsPath!)!;
        Directory.CreateDirectory(dir);

        string temp = Path.Combine(dir, SettingsFileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, _settingsPath!, true); // atomic swap on the same volume
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // Best-effort temp cleanup; surface the original failure below.
            }

            throw;
        }
    }

    // WASM / no-filesystem persistence: flatten the fixed AppSettings shape into the in-memory provider so
    // Current, Reload, and OnChange stay consistent. The whole key set is rebuilt from scratch (clear + set)
    // so a shrinking Library.Folders array or a removed Features.Overrides key leaves NO stale
    // higher-index / removed key that a subsequent bind would re-materialize — the bound AppSettings then
    // exactly matches the written state.
    //
    // DELIBERATELY PARTIAL: only the WASM-reachable subset is flattened. The LiveSync and
    // Highlights sections are EXCLUDED — live sync/reel are AppHostHooks-absent on the browser
    // and the Highlights scan opt-in is CanScan-gated off it, so no browser code path writes
    // them today. If either section ever becomes WASM-reachable, its keys MUST be flattened
    // here too or those writes are silently discarded on reload.
    //
    // ProcessingQueue IS flattened: the queue-management surface + Settings are WASM-reachable, so a
    // browser write of these three scalars must survive a reload.
    private void WriteInMemory(AppSettings settings)
    {
        List<KeyValuePair<string, string?>> data = new()
        {
            new KeyValuePair<string, string?>("Theme", settings.Theme),
            new KeyValuePair<string, string?>("UserCategory", settings.UserCategory.ToString()),
            new KeyValuePair<string, string?>("FirstRunCompleted", settings.FirstRunCompleted ? "true" : "false"),
            new KeyValuePair<string, string?>("LastSeenVersion", settings.LastSeenVersion),
            new KeyValuePair<string, string?>("Features:DeveloperMode", settings.Features.DeveloperMode ? "true" : "false"),
            new KeyValuePair<string, string?>("ProcessingQueue:BackgroundProcessingEnabled",
                settings.ProcessingQueue.BackgroundProcessingEnabled ? "true" : "false"),
            new KeyValuePair<string, string?>("ProcessingQueue:MaxQueueSize",
                settings.ProcessingQueue.MaxQueueSize.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string?>("ProcessingQueue:MaxConcurrency",
                settings.ProcessingQueue.MaxConcurrency.ToString(CultureInfo.InvariantCulture)),
            // Playback2D: every property of the section is flattened here. On WASM there is no file,
            // so a key missing from this list is a setting that silently forgets itself on reload.
            // The export keys are included DELIBERATELY, even though playback2d.export is gated off on
            // browser (B5 D3 overrides B4's original "exclude them" call): a desktop user's default
            // format and output folder are written through the same code path, and an exclusion list
            // that has to be kept in step with a feature gate is a bug waiting for the gate to move.
            new KeyValuePair<string, string?>("Playback2D:LegacyViewport",
                settings.Playback2D.LegacyViewport ? "true" : "false"),
            new KeyValuePair<string, string?>("Playback2D:LastTool", settings.Playback2D.LastTool),
            new KeyValuePair<string, string?>("Playback2D:AnnotationColorArgb",
                settings.Playback2D.AnnotationColorArgb.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string?>("Playback2D:AnnotationWidth",
                settings.Playback2D.AnnotationWidth.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string?>("Playback2D:AnnotationOpacity",
                settings.Playback2D.AnnotationOpacity.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string?>("Playback2D:AnnotationDefaultVisibility",
                settings.Playback2D.AnnotationDefaultVisibility),
            new KeyValuePair<string, string?>("Playback2D:AnnotationFadeInTicks",
                settings.Playback2D.AnnotationFadeInTicks.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string?>("Playback2D:AnnotationFadeOutTicks",
                settings.Playback2D.AnnotationFadeOutTicks.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string?>("Playback2D:AnnotationHoldTicks",
                settings.Playback2D.AnnotationHoldTicks.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string?>("Playback2D:AnnotationAnchorToEntities",
                settings.Playback2D.AnnotationAnchorToEntities ? "true" : "false"),
            new KeyValuePair<string, string?>("Playback2D:AnnotationAutoSave",
                settings.Playback2D.AnnotationAutoSave ? "true" : "false"),
            new KeyValuePair<string, string?>("Playback2D:LevelDisplayMode",
                settings.Playback2D.LevelDisplayMode),
            new KeyValuePair<string, string?>("Playback2D:AutoLevelFollow",
                settings.Playback2D.AutoLevelFollow ? "true" : "false"),
            new KeyValuePair<string, string?>("Playback2D:TimelineShowKills",
                settings.Playback2D.TimelineShowKills ? "true" : "false"),
            new KeyValuePair<string, string?>("Playback2D:TimelineShowBomb",
                settings.Playback2D.TimelineShowBomb ? "true" : "false"),
            new KeyValuePair<string, string?>("Playback2D:TimelineShowAnnotations",
                settings.Playback2D.TimelineShowAnnotations ? "true" : "false"),
            new KeyValuePair<string, string?>("Playback2D:ExportFormatId", settings.Playback2D.ExportFormatId),
            new KeyValuePair<string, string?>("Playback2D:ExportFps",
                settings.Playback2D.ExportFps.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string?>("Playback2D:ExportWidth",
                settings.Playback2D.ExportWidth.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string?>("Playback2D:ExportHeight",
                settings.Playback2D.ExportHeight.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string?>("Playback2D:ExportOutputDirectory",
                settings.Playback2D.ExportOutputDirectory),
            new KeyValuePair<string, string?>("Playback2D:ExportIncludeHud",
                settings.Playback2D.ExportIncludeHud ? "true" : "false"),
            new KeyValuePair<string, string?>("Playback2D:ExportIncludeAnnotations",
                settings.Playback2D.ExportIncludeAnnotations ? "true" : "false"),
            new KeyValuePair<string, string?>("Playback2D:ExportEncoder",
                settings.Playback2D.ExportEncoder),
            new KeyValuePair<string, string?>("Playback2D:ExportQuality",
                settings.Playback2D.ExportQuality)
        };

        for (int i = 0; i < settings.Playback2D.AnnotationRecentColors.Length; i++)
        {
            data.Add(new KeyValuePair<string, string?>($"Playback2D:AnnotationRecentColors:{i}",
                settings.Playback2D.AnnotationRecentColors[i]));
        }

        for (int i = 0; i < settings.Library.Folders.Length; i++)
        {
            data.Add(new KeyValuePair<string, string?>($"Library:Folders:{i}", settings.Library.Folders[i]));
        }

        foreach (KeyValuePair<string, bool> kv in settings.Features.Overrides)
        {
            data.Add(new KeyValuePair<string, string?>(
                $"Features:Overrides:{kv.Key}", kv.Value ? "true" : "false"));
        }

        _mem!.ReplaceAll(data);
    }

    // The persisted state used as a write's mutation basis — the user's FILE values on desktop, the
    // in-memory state on WASM — with the env layer deliberately EXCLUDED (see Write). Robust to a
    // missing/corrupt file: defaults are returned and the write then repairs the file, mirroring the
    // NeedsFirstRun contract.
    private AppSettings ReadPersistedBasis()
    {
        if (_settingsPath is null)
        {
            return BindMemoryOnly(); // WASM: in-memory state only, no env layer
        }

        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            string json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, _serializerOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    // Binds AppSettings from ONLY the in-memory provider's current data (WASM) — a scratch root over a
    // snapshot, so the env-variable provider layered onto the live Configuration is excluded from a
    // write's basis.
    private AppSettings BindMemoryOnly()
    {
        IConfigurationRoot scratch = new ConfigurationBuilder()
            .AddInMemoryCollection(_mem!.Snapshot())
            .Build();
        return scratch.Get<AppSettings>() ?? new AppSettings();
    }

    // A MemoryConfigurationProvider that supports atomic full REPLACEMENT of its data. The built-in
    // MemoryConfigurationProvider exposes only Set/Add (no clear), so a shrinking collection (fewer
    // Library.Folders, a removed Features.Overrides key) would leave stale keys that a subsequent bind
    // re-materializes. Doubles as its own IConfigurationSource so the builder can Add it directly.
    private sealed class ResettableMemoryConfigurationProvider : ConfigurationProvider, IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

        // Atomically swap the whole key set. The owning ConfigurationRoot.Reload() (base Load() is a no-op,
        // so Data survives it) then fires the change token for IOptionsMonitor.OnChange.
        public void ReplaceAll(IEnumerable<KeyValuePair<string, string?>> data)
        {
            Data.Clear();
            foreach (KeyValuePair<string, string?> kv in data)
            {
                Data[kv.Key] = kv.Value;
            }
        }

        // A copy of the current data — the basis for a memory-only (env-excluding) AppSettings bind.
        public KeyValuePair<string, string?>[] Snapshot() => Data.ToArray();
    }
}
