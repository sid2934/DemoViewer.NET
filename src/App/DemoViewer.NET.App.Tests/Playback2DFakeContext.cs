#region

using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A recording <see cref="IModuleContext" /> for the A1 dispatch / follow / adapter tests. Every
///     <c>Request*</c> is logged rather than acted on, which is what lets a test assert "the key routed
///     through the shared clock" instead of "the VM changed some field of its own".
/// </summary>
internal sealed class Playback2DFakeContext : IModuleContext
{
    private readonly List<IPlayerState> _players = [];

    public List<PlayerRosterEntry> Roster { get; } = [];
    public Dictionary<string, List<GameEventView>> Timelines { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int[]> Frames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<int> SeekFrames { get; } = [];
    public List<int> SeekTicks { get; } = [];
    public List<double> Speeds { get; } = [];
    public List<int> SpectateTargets { get; } = [];
    public List<string[]> NextEvents { get; } = [];
    public List<string[]> PrevEvents { get; } = [];
    public int PlayCount { get; private set; }
    public int PauseCount { get; private set; }

    public FakeModuleFeatureGate? Gate { get; set; }

    public bool HasDemo { get; set; } = true;

    // The follow target is slot-keyed and only meaningful inside ONE demo, so Playback2DTabViewModel's
    // resync clears it when this changes. A permanently-null path could not express "a different demo
    // arrived".
    public string? DemoPath { get; set; }
    // The annotation session's tick rate is sourced from this, through the ClockIdentity the tab builds,
    // so a fake that could only ever be 64-tick could not reproduce a tick-rate-dependent bug.
    public int TickRate { get; set; } = 64;
    public int CurrentFrameIndex { get; set; }
    public int CurrentTick { get; set; }
    public bool IsPlaying { get; set; }
    public double Speed { get; set; } = 1.0;
    public int TotalFrames { get; set; } = 1000;
    public bool IsSpeedLocked { get; set; }

    public IModuleFeatureGate? Features => Gate;

    public double CurtimeSeconds(int tick) => tick / (double)TickRate;

    // Two frames per tick, and anything past the frame list resolves to -1 (the "drop this marker" answer).
    public int FrameIndexAtTick(int tick)
    {
        int frame = tick / 2;
        return frame >= TotalFrames ? -1 : frame;
    }

    public IReadOnlyList<int> EventFrames(string eventName) =>
        Frames.TryGetValue(eventName, out int[]? frames) ? frames : Array.Empty<int>();

    public IReadOnlyCollection<string> AvailableEventNames =>
        Timelines.Keys.Concat(Frames.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<GameEventView> GetEventTimeline(string eventName) =>
        Timelines.TryGetValue(eventName, out List<GameEventView>? views)
            ? views
            : Array.Empty<GameEventView>();

    public void RequestSeekToFrame(int frameIndex) => SeekFrames.Add(frameIndex);
    public void RequestSeekToTick(int tick) => SeekTicks.Add(tick);
    public void RequestPlay() => PlayCount++;
    public void RequestPause() => PauseCount++;

    public void RequestSpeed(double speed)
    {
        if (IsSpeedLocked)
        {
            return;
        }

        Speeds.Add(speed);
        Speed = speed;
    }

    public void RequestNextEvent(IReadOnlyCollection<string>? eventNames) =>
        NextEvents.Add(eventNames?.ToArray() ?? []);

    public void RequestPrevEvent(IReadOnlyCollection<string>? eventNames) =>
        PrevEvents.Add(eventNames?.ToArray() ?? []);

    public void NotifySpectateTarget(int slot) => SpectateTargets.Add(slot);

    public event Action<IPlaybackSnapshot>? Advanced;
    public event Action? DemoReset;

    public IReadOnlyEntityView Entities { get; } = new EmptyEntityView();
    public IReadOnlyList<PlayerRosterEntry> Players => Roster;
    public IReadOnlyList<IPlayerState> CurrentPlayers => _players;

    /// <summary>
    ///     Empties the roster and the live player states. The demo-swap half of a <c>DemoReset</c>:
    ///     <see cref="Roster" /> alone is not the whole roster, and leaving <c>_players</c> behind makes a
    ///     "new demo" that still carries the old one's live states.
    /// </summary>
    public void ClearPlayers()
    {
        Roster.Clear();
        _players.Clear();
    }

    /// <summary>Adds one roster entry plus the matching live player state (team 2 = T, 3 = CT).</summary>
    public void AddPlayer(int slot, string name, int team)
    {
        Roster.Add(new PlayerRosterEntry
        {
            Slot = slot,
            Name = name,
            SteamId = (ulong)(slot + 1)
        });
        _players.Add(new FakePlayerState(slot, team));
    }

    public void Push(int frameIndex, int tick)
    {
        CurrentFrameIndex = frameIndex;
        CurrentTick = tick;
        Advanced?.Invoke(new FakeSnapshot(frameIndex, tick, Entities, _players));
    }

    /// <summary>
    ///     Pushes one snapshot of ALIVE players at explicit world positions, advancing the frame and
    ///     tick. The default <see cref="AddPlayer" /> states carry a field-less pawn, which the scene
    ///     builder correctly reads as "not alive" — no discs, no rings, nothing to look at. This is the
    ///     entry point the render tests use.
    /// </summary>
    /// <param name="markers">Slot, team, world X/Y/Z and yaw per player.</param>
    public void PushMarkers(params (int Slot, int Team, float X, float Y, float Z, float Yaw)[] markers)
    {
        ArgumentNullException.ThrowIfNull(markers);

        List<IPlayerState> states = new(markers.Length);
        foreach ((int slot, int team, float x, float y, float z, float yaw) in markers)
        {
            states.Add(LivePlayerState.Alive(slot, team, x, y, z, yaw));
        }

        CurrentFrameIndex++;
        CurrentTick += 2;
        Advanced?.Invoke(new FakeSnapshot(CurrentFrameIndex, CurrentTick, Entities, states));
    }

    public void RaiseDemoReset() => DemoReset?.Invoke();

    private sealed class FakeSnapshot(
        int frameIndex, int tick, IReadOnlyEntityView entities, IReadOnlyList<IPlayerState> players)
        : IPlaybackSnapshot
    {
        public int FrameIndex => frameIndex;
        public int Tick => tick;
        public IReadOnlyEntityView Entities => entities;
        public IReadOnlyList<IPlayerState> Players => players;
    }

    private sealed class FakePlayerState(int slot, int team) : IPlayerState
    {
        public int Slot => slot;
        public int Team => team;
        public bool HasLivePawn => true;
        public IReadOnlyEntity? Pawn { get; } = new FakeEntity("CCSPlayerPawn");
        public IReadOnlyEntity? Controller { get; } = new FakeEntity("CCSPlayerController");
        public (float X, float Y, float Z)? WorldPosition => (slot * 100f, slot * 50f, 64f);
    }

    private sealed class FakeEntity(string className) : IReadOnlyEntity
    {
        public string ClassName => className;
        public int Serial => 1;
        public bool IsInPvs => true;
        public object? this[string fieldPath] => null;

        public bool TryGet<T>(string fieldPath, out T value)
        {
            value = default!;
            return false;
        }
    }

    /// <summary>A live pawn with the handful of fields the scene builder actually reads.</summary>
    private sealed class LivePlayerState : IPlayerState
    {
        private LivePlayerState(int slot, int team, IReadOnlyEntity pawn, IReadOnlyEntity controller,
            (float X, float Y, float Z) position)
        {
            Slot = slot;
            Team = team;
            Pawn = pawn;
            Controller = controller;
            WorldPosition = position;
        }

        public int Slot { get; }
        public int Team { get; }
        public bool HasLivePawn => true;
        public IReadOnlyEntity? Pawn { get; }
        public IReadOnlyEntity? Controller { get; }
        public (float X, float Y, float Z)? WorldPosition { get; }

        public static LivePlayerState Alive(int slot, int team, float x, float y, float z, float yaw)
        {
            FieldEntity pawn = new("CCSPlayerPawn");
            pawn.Fields["m_iHealth"] = 100;
            pawn.Fields["m_iShotsFired"] = 0;
            pawn.Fields["m_lifeState"] = 0;
            pawn.Fields["m_flFlashDuration"] = 0f;
            pawn.Fields["m_angEyeAngles"] = new System.Numerics.Vector3(0, yaw, 0);
            pawn.Fields["m_ArmorValue"] = 100;

            FieldEntity controller = new("CCSPlayerController");
            return new LivePlayerState(slot, team, pawn, controller, (x, y, z));
        }
    }

    private sealed class FieldEntity(string className) : IReadOnlyEntity
    {
        public Dictionary<string, object?> Fields { get; } = [];
        public string ClassName => className;
        public int Serial => 1;
        public bool IsInPvs => true;
        public object? this[string fieldPath] => Fields.GetValueOrDefault(fieldPath);

        public bool TryGet<T>(string fieldPath, out T value)
        {
            if (Fields.TryGetValue(fieldPath, out object? found) && found is T typed)
            {
                value = typed;
                return true;
            }

            value = default!;
            return false;
        }
    }

    private sealed class EmptyEntityView : IReadOnlyEntityView
    {
        public IEnumerable<IReadOnlyEntity> All() => Array.Empty<IReadOnlyEntity>();
        public IEnumerable<IReadOnlyEntity> OfClass(string className) => Array.Empty<IReadOnlyEntity>();
        public IReadOnlyEntity? BySerial(int serial) => null;
        public IReadOnlyEntity? ByIndex(int entityIndex) => null;
        public IReadOnlyEntity? ResolveHandle(ulong handle) => null;
    }
}

/// <summary>A settable <see cref="IModuleFeatureGate" /> so a test can flip a gate and watch the tab react.</summary>
internal sealed class FakeModuleFeatureGate : IModuleFeatureGate
{
    private readonly HashSet<string> _off = new(StringComparer.Ordinal);

    public bool IsEnabled(string featureId) => !_off.Contains(featureId);

    public event Action? Changed;

    public void SetEnabled(string featureId, bool enabled)
    {
        if (enabled)
        {
            _off.Remove(featureId);
        }
        else
        {
            _off.Add(featureId);
        }

        Changed?.Invoke();
    }
}
