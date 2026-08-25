#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DemoViewer.NET.Playback2D.Core.Annotations;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Annotations;

/// <summary>
///     Reads and writes the <c>.dvann.json</c> annotation sidecar.
///     <para>
///         <b>Two locations, in order.</b> Beside the demo when its directory is writable — that is where
///         a user expects a file they can hand to a teammate along with the demo — otherwise under the
///         app-data root, keyed by the demo's SHA-256. A read-only Steam replay folder is the common
///         case, not the exception. With neither available (WASM) the store is inert and says so, and
///         the session's annotations live only as long as the tab does.
///     </para>
///     <para>
///         <b>Identity is checked, never assumed.</b> A demo-hash mismatch means the file describes a
///         different demo that happens to share a path: it is ignored and never overwritten. A clock
///         mismatch means the tick anchors were authored against a different parse: everything loads,
///         static elements are unaffected, and the caller warns instead of silently mis-placing every
///         time-anchored stroke.
///     </para>
///     <para>
///         <b>Writes are best-effort and never throw into the UI</b>, matching the existing
///         <c>GraphBreakpointStore.Save</c> and <c>SettingsService.SaveSession</c> behaviour: a failed
///         write becomes <c>false</c> plus a status string, never an exception mid-gesture.
///     </para>
/// </summary>
public sealed class AnnotationStore
{
    /// <summary>The sidecar schema version this build writes.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The sidecar's extension, appended to the demo's full file name.</summary>
    public const string SidecarExtension = ".dvann.json";

    private readonly string? _appDataRoot;
    private readonly Func<string, string> _demoKeyResolver;

    // Every dictionary below is reached from BOTH the UI thread (ResolvePath, for the panel's status
    // line) and a thread-pool thread (the debounced autosave, and LoadAsync's continuation). A plain
    // Dictionary read racing a write does not merely lose an entry — it can spin forever inside bucket
    // traversal — so all three live behind one gate. They are touched once per demo, never per frame.
    private readonly Lock _state = new();

    private readonly Dictionary<string, bool> _writableByDirectory = new(StringComparer.OrdinalIgnoreCase);

    // Unknown JSON preserved from the last load of a given path, re-emitted on the next save so a v2
    // field written by a newer build survives a v1 round trip.
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _rootExtras =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Dictionary<Guid, Dictionary<string, JsonElement>>> _elementExtras =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a store.</summary>
    /// <param name="appDataRoot">
    ///     App-data root for the fallback location. Null = no fallback (WASM), which makes the store
    ///     inert whenever the demo's own directory is not writable. Pipeline must not reference the App,
    ///     so the App passes <c>AppPaths.ConfigRoot</c> in.
    /// </param>
    /// <param name="demoKeyResolver">
    ///     Demo path → lowercase-hex SHA-256. Injected so the App can pass its already-cached hash:
    ///     SHA-256 over a multi-GB <c>.dem</c> is not free, and nothing in the annotation path may hash
    ///     on the UI thread. Defaults to a streaming hash for the CLI.
    /// </param>
    public AnnotationStore(string? appDataRoot, Func<string, string>? demoKeyResolver = null)
    {
        _appDataRoot = string.IsNullOrWhiteSpace(appDataRoot) ? null : appDataRoot;
        _demoKeyResolver = demoKeyResolver ?? ComputeDemoKey;
    }

    /// <summary>False when nothing can be persisted at all — no app-data root on a read-only demo dir.</summary>
    public bool IsPersistent => _appDataRoot is not null;

    /// <summary>Computes a demo's identity, streaming the file rather than reading it into memory.</summary>
    /// <param name="demoPath">Path to the <c>.dem</c>.</param>
    public static DemoIdentity IdentityFor(string demoPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(demoPath);

        long size = 0;
        try
        {
            size = new FileInfo(demoPath).Length;
        }
        catch (IOException)
        {
            // Diagnostics only; a size we cannot read is not a reason to fail.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new DemoIdentity(ComputeDemoKey(demoPath), Path.GetFileName(demoPath), size);
    }

    /// <summary>Lowercase-hex SHA-256 of a file's bytes, streamed. The existing repo-wide demo key.</summary>
    /// <param name="path">The file to hash.</param>
    public static string ComputeDemoKey(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>Where this demo's sidecar would be written.</summary>
    /// <param name="demoPath">Path to the <c>.dem</c>.</param>
    public AnnotationStoreLocation ResolveLocation(string demoPath)
    {
        if (string.IsNullOrWhiteSpace(demoPath))
        {
            return _appDataRoot is null ? AnnotationStoreLocation.None : AnnotationStoreLocation.AppData;
        }

        if (IsDirectoryWritable(Path.GetDirectoryName(Path.GetFullPath(demoPath))))
        {
            return AnnotationStoreLocation.DemoSidecar;
        }

        return _appDataRoot is null ? AnnotationStoreLocation.None : AnnotationStoreLocation.AppData;
    }

    /// <summary>The file this demo's sidecar would be written to, or null when nothing can be.</summary>
    /// <param name="demoPath">Path to the <c>.dem</c>.</param>
    public string? ResolvePath(string demoPath) => ResolveLocation(demoPath) switch
    {
        AnnotationStoreLocation.DemoSidecar => Path.GetFullPath(demoPath) + SidecarExtension,
        AnnotationStoreLocation.AppData => Path.Combine(_appDataRoot!, "annotations",
            _demoKeyResolver(demoPath) + SidecarExtension),
        _ => null
    };

    /// <summary>
    ///     Loads a demo's sidecar. Never throws for a missing, truncated or foreign file — a corrupt
    ///     sidecar must not stop a demo from opening.
    /// </summary>
    /// <param name="demoPath">Path to the <c>.dem</c>.</param>
    /// <param name="clock">The clock the caller's parse is on.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<AnnotationLoadResult> LoadAsync(string demoPath, ClockIdentity clock,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clock);

        AnnotationStoreLocation location = ResolveLocation(demoPath);
        string? path = ResolvePath(demoPath);
        if (path is null || !File.Exists(path))
        {
            return AnnotationLoadResult.Empty(location, path);
        }

        AnnotationDocumentDto? dto;
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 16, useAsync: true);
            dto = await JsonSerializer
                .DeserializeAsync(stream, AnnotationJsonContext.Default.AnnotationDocumentDto, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return AnnotationLoadResult.Empty(location, path);
        }
        catch (IOException)
        {
            return AnnotationLoadResult.Empty(location, path);
        }
        catch (UnauthorizedAccessException)
        {
            return AnnotationLoadResult.Empty(location, path);
        }

        if (dto is null)
        {
            return AnnotationLoadResult.Empty(location, path);
        }

        // A hash mismatch means the file belongs to a DIFFERENT demo that happens to share this path.
        // Ignore it entirely — and remember nothing about it, so the next save cannot merge its extras
        // into someone else's document or overwrite it.
        bool demoMismatch = false;
        if (dto.Demo?.Sha256 is { Length: > 0 } recorded && location == AnnotationStoreLocation.DemoSidecar)
        {
            string actual = _demoKeyResolver(demoPath);
            if (actual.Length > 0 && !string.Equals(actual, recorded, StringComparison.OrdinalIgnoreCase))
            {
                demoMismatch = true;
            }
        }

        if (demoMismatch)
        {
            Forget(path);
            return new AnnotationLoadResult([], location, path, true, false,
                dto.SchemaVersion);
        }

        bool clockMismatch = !clock.Matches(ToClock(dto.Clock));

        List<AnnotationElement> elements = [];
        Dictionary<Guid, Dictionary<string, JsonElement>> elementExtras = [];

        foreach (AnnotationElementDto element in dto.Elements ?? [])
        {
            if (ToElement(element) is not { } parsed)
            {
                continue;
            }

            elements.Add(parsed);
            if (element.Extra is { Count: > 0 } extra)
            {
                elementExtras[parsed.Id] = extra;
            }
        }

        lock (_state)
        {
            _rootExtras[path] = dto.Extra ?? [];
            _elementExtras[path] = elementExtras;
        }

        return new AnnotationLoadResult(elements, location, path, false, clockMismatch, dto.SchemaVersion);
    }

    /// <summary>
    ///     Writes a demo's sidecar atomically (temp file plus a replace), mirroring the settings writer.
    ///     Best-effort: returns false on any I/O failure rather than throwing into the caller.
    /// </summary>
    /// <param name="demoPath">Path to the <c>.dem</c>.</param>
    /// <param name="demo">The demo's identity.</param>
    /// <param name="clock">The clock the anchors were authored against.</param>
    /// <param name="elements">The elements to persist.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> SaveAsync(string demoPath, DemoIdentity demo, ClockIdentity clock,
        IReadOnlyList<AnnotationElement> elements, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(elements);

        string? path = ResolvePath(demoPath);
        if (path is null)
        {
            return false;
        }

        Dictionary<string, JsonElement>? rootExtra;
        Dictionary<Guid, Dictionary<string, JsonElement>>? elementExtras;
        lock (_state)
        {
            _rootExtras.TryGetValue(path, out rootExtra);
            _elementExtras.TryGetValue(path, out elementExtras);
        }

        AnnotationDocumentDto dto = new()
        {
            SchemaVersion = SchemaVersion,
            Demo = new DemoIdentityDto
            {
                Sha256 = demo.Sha256,
                FileName = demo.FileName,
                SizeBytes = demo.SizeBytes
            },
            Clock = new ClockIdentityDto
            {
                Kind = clock.Kind,
                TickRate = clock.TickRate,
                FrameCount = clock.FrameCount,
                FirstTick = clock.FirstTick,
                LastTick = clock.LastTick
            },
            Elements = new List<AnnotationElementDto>(elements.Count),
            Extra = rootExtra is { Count: > 0 } ? rootExtra : null
        };

        for (int i = 0; i < elements.Count; i++)
        {
            AnnotationElementDto element = ToDto(elements[i]);
            if (elementExtras is not null
                && elementExtras.TryGetValue(elements[i].Id, out Dictionary<string, JsonElement>? extra)
                && extra.Count > 0)
            {
                element.Extra = extra;
            }

            dto.Elements.Add(element);
        }

        string temp = path + ".tmp";
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 1 << 16, useAsync: true))
            {
                await JsonSerializer
                    .SerializeAsync(stream, dto, AnnotationJsonContext.Default.AnnotationDocumentDto, ct)
                    .ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            TryDelete(temp);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temp);
            return false;
        }
        catch (NotSupportedException)
        {
            TryDelete(temp);
            return false;
        }
    }

    /// <summary>Deletes a demo's sidecar. Best-effort; false when there was nothing to delete.</summary>
    /// <param name="demoPath">Path to the <c>.dem</c>.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<bool> DeleteAsync(string demoPath, CancellationToken ct = default)
    {
        string? path = ResolvePath(demoPath);
        if (path is null || !File.Exists(path))
        {
            return Task.FromResult(false);
        }

        Forget(path);

        try
        {
            File.Delete(path);
            return Task.FromResult(true);
        }
        catch (IOException)
        {
            return Task.FromResult(false);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
    }

    private void Forget(string path)
    {
        lock (_state)
        {
            _rootExtras.Remove(path);
            _elementExtras.Remove(path);
        }
    }

    // Probe once per directory per session and cache: probing on every save is slow on a network share,
    // and a read-only Steam replay folder is the common case rather than the exception.
    private bool IsDirectoryWritable(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        lock (_state)
        {
            if (_writableByDirectory.TryGetValue(directory, out bool cached))
            {
                return cached;
            }
        }

        // Probe OUTSIDE the gate: a create+delete on a dead network share can block for seconds, and
        // holding the lock across it would stall the UI thread's status line behind an autosave.
        // A concurrent duplicate probe is harmless — same answer, one extra temp file.
        bool writable = Probe(directory);
        lock (_state)
        {
            _writableByDirectory[directory] = writable;
        }

        return writable;
    }

    private static bool Probe(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        string probe = Path.Combine(directory,
            ".dvann.probe." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            using (FileStream stream = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
            return true;
        }
        catch (IOException)
        {
            TryDelete(probe);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(probe);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; the caller's own failure is what matters.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ClockIdentity? ToClock(ClockIdentityDto? dto) =>
        dto is null
            ? null
            : new ClockIdentity(dto.Kind ?? ClockIdentity.DvFrameClock, dto.TickRate, dto.FrameCount,
                dto.FirstTick, dto.LastTick);

    private static AnnotationElementDto ToDto(AnnotationElement element)
    {
        AnnotationElementDto dto = new()
        {
            Id = element.Id.ToString("D", CultureInfo.InvariantCulture),
            Kind = element.Kind.ToString(),
            ColorArgb = element.Style.ColorArgb,
            WidthWorld = element.Style.WidthWorld,
            Opacity = element.Style.Opacity,
            RevealOnFadeIn = element.Style.RevealOnFadeIn,
            FromTick = element.Time.FromTick,
            UntilTick = element.Time.UntilTick,
            FadeInTicks = element.Time.FadeInTicks,
            FadeOutTicks = element.Time.FadeOutTicks,
            Text = element.Text,
            Points = new List<float>(element.Points.Count * 3)
        };

        switch (element.Space)
        {
            case SpaceRef.Entity entity:
                dto.Space = "entity";
                dto.SteamId = entity.SteamId;
                dto.Dx = entity.Dx;
                dto.Dy = entity.Dy;
                break;

            case SpaceRef.World world:
                dto.Space = "world";
                dto.LevelMinZ = world.LevelMinZ;
                break;

            default:
                dto.Space = "world";
                break;
        }

        for (int i = 0; i < element.Points.Count; i++)
        {
            InkPoint point = element.Points[i];
            dto.Points.Add(point.X);
            dto.Points.Add(point.Y);
            dto.Points.Add(point.Pressure);
        }

        return dto;
    }

    private static AnnotationElement? ToElement(AnnotationElementDto dto)
    {
        if (dto is null || !Guid.TryParse(dto.Id, out Guid id))
        {
            return null;
        }

        if (!Enum.TryParse(dto.Kind, ignoreCase: true, out AnnotationKind kind))
        {
            kind = AnnotationKind.Freehand;
        }

        SpaceRef space = string.Equals(dto.Space, "entity", StringComparison.OrdinalIgnoreCase)
            ? new SpaceRef.Entity(dto.SteamId, dto.Dx, dto.Dy)
            : new SpaceRef.World(dto.LevelMinZ);

        List<float> flat = dto.Points ?? [];
        int count = flat.Count / 3;
        InkPoint[] points = new InkPoint[count];
        for (int i = 0; i < count; i++)
        {
            points[i] = new InkPoint(flat[i * 3], flat[(i * 3) + 1], flat[(i * 3) + 2]);
        }

        return new AnnotationElement(id, kind,
            new AnnotationStyle(dto.ColorArgb, dto.WidthWorld, dto.Opacity, dto.RevealOnFadeIn),
            space,
            new TimeEnvelope(dto.FromTick, dto.UntilTick, dto.FadeInTicks, dto.FadeOutTicks),
            points,
            dto.Text);
    }
}
