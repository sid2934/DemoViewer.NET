#region

using System.Text.Json;
using DemoViewer.NET.Playback2D.Core;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline;

/// <summary>
///     Reads and writes <see cref="SceneFixture" /> JSON through a source-generated context, so the
///     format works under trimming and on WASM.
///     <para>
///         <b>Tolerant reader.</b> Members this build does not know are captured and re-emitted
///         unchanged, so a fixture written by a newer build survives a round trip here rather than being
///         silently truncated. Enum values are written as names and read case-insensitively; an
///         unrecognised name falls back to the enum's zero value rather than throwing, because a corpus
///         fixture is data, not code.
///     </para>
/// </summary>
public static class SceneFixtureSerializer
{
    // Indented output with an EXPLICIT LF. JsonWriterOptions.NewLine defaults to Environment.NewLine, so
    // the same fixture written on Windows and on Linux differed in every line ending — and the corpus is
    // committed text that .gitattributes pins to LF (eol=lf). The visible symptom was
    // tests/fixtures/playback2d/scenes/nuke-multilevel.scene.json turning up CRLF in the working tree
    // after every Windows App-suite run: staging normalised it back, so nothing ever reached a commit and
    // nothing ever stopped happening either. Recorded at the B2 merge (deviation 35); fixed here, because
    // "your checkout is dirty and it does not matter" is a thing every contributor has to learn once.
    private static readonly JsonWriterOptions _writerOptions = new()
    {
        Indented = true,
        NewLine = "\n"
    };

    /// <summary>Reads a fixture from a stream.</summary>
    /// <param name="source">The stream to read. Not closed.</param>
    /// <exception cref="JsonException">The payload is not a scene fixture.</exception>
    public static SceneFixture Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        SceneFixtureDto dto =
            JsonSerializer.Deserialize(source, SceneFixtureJsonContext.Default.SceneFixtureDto)
            ?? throw new JsonException("Scene fixture payload was null.");
        return FromDto(dto);
    }

    /// <summary>Reads a fixture from a file.</summary>
    /// <param name="path">Path to the <c>.scene.json</c> file.</param>
    public static SceneFixture ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>Writes a fixture to a stream. Indented, with LF line endings on every platform.</summary>
    /// <param name="fixture">The fixture to write.</param>
    /// <param name="destination">The stream written to. Not closed.</param>
    public static void Write(SceneFixture fixture, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(destination);

        using Utf8JsonWriter writer = new(destination, _writerOptions);
        JsonSerializer.Serialize(writer, ToDto(fixture), SceneFixtureJsonContext.Default.SceneFixtureDto);
    }

    /// <summary>Writes a fixture to a file, creating the directory if needed.</summary>
    /// <param name="fixture">The fixture to write.</param>
    /// <param name="path">Path to the <c>.scene.json</c> file.</param>
    public static void WriteFile(SceneFixture fixture, string path)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentException.ThrowIfNullOrEmpty(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = File.Create(path);
        Write(fixture, stream);
    }

    // ── DTO → model ─────────────────────────────────────────────────────────────────────────────────

    private static SceneFixture FromDto(SceneFixtureDto dto) => new()
    {
        SchemaVersion = dto.SchemaVersion ?? SceneFixture.CurrentSchemaVersion,
        Time = FromDto(dto.Time),
        Camera = FromDto(dto.Camera),
        Size = dto.Size is { } s ? new SKSizeI(s.Width, s.Height) : default,
        MapName = dto.MapName,
        MapVersion = dto.MapVersion,
        Annotations = dto.Annotations,
        SourceDemoId = dto.SourceDemoId,
        Notes = dto.Notes,
        Extra = dto.Extra,
        Frame = FromDto(dto.Frame)
    };

    private static Scene2DFrame FromDto(Scene2DFrameDto? dto)
    {
        if (dto is null)
        {
            return Scene2DFrame.Empty;
        }

        return new Scene2DFrame
        {
            Time = FromDto(dto.Time),
            Markers = MapList(dto.Markers, FromDto),
            AreaEffects = MapList(dto.AreaEffects, FromDto),
            Trails = MapList(dto.Trails, FromDto),
            Bomb = dto.Bomb is { } b
                ? new BombMarker(b.WorldX, b.WorldY, b.WorldZ, b.DetonationFraction, b.BeingDefused,
                    b.DefuseFraction)
                : null,
            KillFeed = MapList(dto.KillFeed, FromDto),
            GameInfo = FromDto(dto.GameInfo),
            Map = FromDto(dto.Map),
            Vision = FromDto(dto.Vision),
            FollowSlot = dto.FollowSlot ?? -1
        };
    }

    private static SceneTime FromDto(SceneTimeDto? dto) => dto is null
        ? default
        : new SceneTime(dto.Tick, dto.FrameIndex, dto.DemoSeconds, dto.DeltaSeconds, dto.IsDiscontinuity);

    private static ViewportTransform FromDto(ViewportTransformDto? dto) => dto is null
        ? default
        : new ViewportTransform(dto.ViewWidth, dto.ViewHeight, dto.CenterX, dto.CenterY, dto.BaseScale,
            dto.Zoom, dto.PanX, dto.PanY);

    private static PlayerMarker FromDto(PlayerMarkerDto dto) => new(
        dto.Slot, dto.Team, dto.WorldX, dto.WorldY, dto.WorldZ, dto.YawDegrees,
        ParseEnum<RingState>(dto.Ring), dto.RingAlpha, dto.Label ?? "", dto.IsAlive,
        dto.PitchDegrees, dto.DuckAmount, dto.SteamId);

    private static AreaEffect FromDto(AreaEffectDto dto) => new(
        ParseEnum<AreaEffectKind>(dto.Kind), dto.WorldX, dto.WorldY, dto.WorldZ, dto.WorldRadius);

    private static GrenadeTrail FromDto(GrenadeTrailDto dto)
    {
        GrenadeTrail trail = new()
        {
            Kind = ParseEnum<GrenadeKind>(dto.Kind),
            LastTick = dto.LastTick,
            Alpha = dto.Alpha
        };

        if (dto.Points is { } points)
        {
            foreach (TrailPointDto p in points)
            {
                trail.Points.Add(new GrenadeTrailPoint(p.X, p.Y, p.Z));
            }
        }

        return trail;
    }

    private static KillFeedRow FromDto(KillFeedRowDto dto) => new(
        dto.Tick, dto.Attacker ?? "", dto.Assister, dto.Victim ?? "", dto.Weapon ?? "",
        dto.Headshot, dto.Penetrated, dto.NoScope, dto.ThroughSmoke, dto.AttackerBlind,
        dto.AttackerInAir, dto.AssistedFlash);

    private static SceneGameInfo FromDto(SceneGameInfoDto? dto) => dto is null
        ? SceneGameInfo.Empty
        : new SceneGameInfo(dto.Phase ?? "—", dto.BombState ?? "—", dto.RoundNumber, dto.RoundsPlayed,
            dto.RoundSeconds, dto.RoundTime ?? "—", dto.BombTicking, dto.DefuseInProgress,
            dto.DefuseKitNote ?? "—", dto.DefuseSeconds, dto.DefuseTime ?? "—", dto.TScore, dto.CtScore);

    private static SceneMapInfo FromDto(SceneMapInfoDto? dto)
    {
        if (dto is null)
        {
            return SceneMapInfo.Unknown;
        }

        return new SceneMapInfo
        {
            MapName = dto.MapName ?? "",
            NetworkedBounds = FromDtoOrNull(dto.NetworkedBounds),
            ObservedBounds = FromDtoOrNull(dto.ObservedBounds) ?? WorldBounds.Default,
            SectionHeights = dto.SectionHeights,
            Radars = MapList(dto.Radars, FromDto)
        };
    }

    private static MapRadarImage FromDto(MapRadarImageDto dto) => new()
    {
        Name = dto.Name ?? "",
        Bounds = FromDtoOrNull(dto.Bounds) ?? WorldBounds.Default,
        MinZ = dto.MinZ,
        MaxZ = dto.MaxZ
    };

    private static SceneVision FromDto(SceneVisionDto? dto)
    {
        if (dto is null)
        {
            return SceneVision.Off;
        }

        return new SceneVision
        {
            IsAvailable = dto.IsAvailable,
            Cones = MapList(dto.Cones, FromDto),
            Sightlines = MapList(dto.Sightlines, FromDto)
        };
    }

    private static VisionCone FromDto(VisionConeDto dto) => new()
    {
        Slot = dto.Slot,
        Team = dto.Team,
        ApexX = dto.ApexX,
        ApexY = dto.ApexY,
        ApexZ = dto.ApexZ,
        Fan = MapList(dto.Fan, static p => new ConePoint(p.X, p.Y))
    };

    private static Sightline FromDto(SightlineDto dto) => new(
        dto.ViewerSlot, dto.ViewerTeam, dto.X0, dto.Y0, dto.Z0, dto.X1, dto.Y1, dto.Z1);

    private static WorldBounds? FromDtoOrNull(WorldBoundsDto? dto) =>
        dto is null ? null : new WorldBounds(dto.MinX, dto.MinY, dto.MaxX, dto.MaxY);

    // ── model → DTO ─────────────────────────────────────────────────────────────────────────────────

    private static SceneFixtureDto ToDto(SceneFixture fixture) => new()
    {
        SchemaVersion = fixture.SchemaVersion,
        Time = ToDto(fixture.Time),
        Camera = ToDto(fixture.Camera),
        Size = new SizeDto
        {
            Width = fixture.Size.Width,
            Height = fixture.Size.Height
        },
        MapName = fixture.MapName,
        MapVersion = fixture.MapVersion,
        Annotations = fixture.Annotations,
        SourceDemoId = fixture.SourceDemoId,
        Notes = fixture.Notes,
        Frame = ToDto(fixture.Frame),
        Extra = fixture.Extra
    };

    private static Scene2DFrameDto ToDto(Scene2DFrame frame) => new()
    {
        Time = ToDto(frame.Time),
        Markers = MapList(frame.Markers, ToDto),
        AreaEffects = MapList(frame.AreaEffects, ToDto),
        Trails = MapList(frame.Trails, ToDto),
        Bomb = frame.Bomb is { } b
            ? new BombMarkerDto
            {
                WorldX = b.WorldX,
                WorldY = b.WorldY,
                WorldZ = b.WorldZ,
                DetonationFraction = b.DetonationFraction,
                BeingDefused = b.BeingDefused,
                DefuseFraction = b.DefuseFraction
            }
            : null,
        KillFeed = MapList(frame.KillFeed, ToDto),
        GameInfo = ToDto(frame.GameInfo),
        Map = ToDto(frame.Map),
        Vision = ToDto(frame.Vision),
        FollowSlot = frame.FollowSlot
    };

    private static SceneTimeDto ToDto(SceneTime t) => new()
    {
        Tick = t.Tick,
        FrameIndex = t.FrameIndex,
        DemoSeconds = t.DemoSeconds,
        DeltaSeconds = t.DeltaSeconds,
        IsDiscontinuity = t.IsDiscontinuity
    };

    private static ViewportTransformDto ToDto(ViewportTransform t) => new()
    {
        ViewWidth = t.ViewWidth,
        ViewHeight = t.ViewHeight,
        CenterX = t.CenterX,
        CenterY = t.CenterY,
        BaseScale = t.BaseScale,
        Zoom = t.Zoom,
        PanX = t.PanX,
        PanY = t.PanY
    };

    private static PlayerMarkerDto ToDto(PlayerMarker m) => new()
    {
        Slot = m.Slot,
        Team = m.Team,
        WorldX = m.WorldX,
        WorldY = m.WorldY,
        WorldZ = m.WorldZ,
        YawDegrees = m.YawDegrees,
        Ring = m.Ring.ToString(),
        RingAlpha = m.RingAlpha,
        Label = m.Label,
        IsAlive = m.IsAlive,
        PitchDegrees = m.PitchDegrees,
        DuckAmount = m.DuckAmount,
        SteamId = m.SteamId
    };

    private static AreaEffectDto ToDto(AreaEffect a) => new()
    {
        Kind = a.Kind.ToString(),
        WorldX = a.WorldX,
        WorldY = a.WorldY,
        WorldZ = a.WorldZ,
        WorldRadius = a.WorldRadius
    };

    private static GrenadeTrailDto ToDto(GrenadeTrail t) => new()
    {
        Kind = t.Kind.ToString(),
        LastTick = t.LastTick,
        Alpha = t.Alpha,
        Points = MapList(t.Points, static p => new TrailPointDto
        {
            X = p.X,
            Y = p.Y,
            Z = p.Z
        })
    };

    private static KillFeedRowDto ToDto(KillFeedRow k) => new()
    {
        Tick = k.Tick,
        Attacker = k.Attacker,
        Assister = k.Assister,
        Victim = k.Victim,
        Weapon = k.Weapon,
        Headshot = k.Headshot,
        Penetrated = k.Penetrated,
        NoScope = k.NoScope,
        ThroughSmoke = k.ThroughSmoke,
        AttackerBlind = k.AttackerBlind,
        AttackerInAir = k.AttackerInAir,
        AssistedFlash = k.AssistedFlash
    };

    private static SceneGameInfoDto ToDto(SceneGameInfo g) => new()
    {
        Phase = g.Phase,
        BombState = g.BombState,
        RoundNumber = g.RoundNumber,
        RoundsPlayed = g.RoundsPlayed,
        RoundSeconds = g.RoundSeconds,
        RoundTime = g.RoundTime,
        BombTicking = g.BombTicking,
        DefuseInProgress = g.DefuseInProgress,
        DefuseKitNote = g.DefuseKitNote,
        DefuseSeconds = g.DefuseSeconds,
        DefuseTime = g.DefuseTime,
        TScore = g.TScore,
        CtScore = g.CtScore
    };

    private static SceneMapInfoDto ToDto(SceneMapInfo m) => new()
    {
        MapName = m.MapName,
        NetworkedBounds = ToDtoOrNull(m.NetworkedBounds),
        ObservedBounds = ToDtoOrNull(m.ObservedBounds),
        SectionHeights = m.SectionHeights is null ? null : [.. m.SectionHeights],
        Radars = MapList(m.Radars, static r => new MapRadarImageDto
        {
            Name = r.Name,
            Bounds = ToDtoOrNull(r.Bounds),
            MinZ = r.MinZ,
            MaxZ = r.MaxZ
        })
    };

    private static SceneVisionDto ToDto(SceneVision v) => new()
    {
        IsAvailable = v.IsAvailable,
        Cones = MapList(v.Cones, static c => new VisionConeDto
        {
            Slot = c.Slot,
            Team = c.Team,
            ApexX = c.ApexX,
            ApexY = c.ApexY,
            ApexZ = c.ApexZ,
            Fan = MapList(c.Fan, static p => new ConePointDto
            {
                X = p.X,
                Y = p.Y
            })
        }),
        Sightlines = MapList(v.Sightlines, static s => new SightlineDto
        {
            ViewerSlot = s.ViewerSlot,
            ViewerTeam = s.ViewerTeam,
            X0 = s.X0,
            Y0 = s.Y0,
            Z0 = s.Z0,
            X1 = s.X1,
            Y1 = s.Y1,
            Z1 = s.Z1
        })
    };

    private static WorldBoundsDto? ToDtoOrNull(WorldBounds? bounds) => bounds is not { } b
        ? null
        : new WorldBoundsDto
        {
            MinX = b.MinX,
            MinY = b.MinY,
            MaxX = b.MaxX,
            MaxY = b.MaxY
        };

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    // One projection helper for both directions: it takes any read-only list (or null) and always
    // returns a concrete List, because the DTO properties are List-typed and the model properties are
    // IReadOnlyList-typed, and a List satisfies both.
    private static List<TOut> MapList<TIn, TOut>(IReadOnlyList<TIn>? source, Func<TIn, TOut> project)
    {
        if (source is null || source.Count == 0)
        {
            return [];
        }

        List<TOut> result = new(source.Count);
        foreach (TIn item in source)
        {
            result.Add(project(item));
        }

        return result;
    }

    // A corpus fixture is data, not code: an enum name this build does not know falls back to the zero
    // value rather than throwing, so one stale member cannot make a whole fixture unreadable.
    private static TEnum ParseEnum<TEnum>(string? name) where TEnum : struct, Enum =>
        Enum.TryParse(name, true, out TEnum value) ? value : default;
}
