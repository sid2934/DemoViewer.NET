#region

using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Models;
using Cs2DemoKit.Parser.Entities;
using Cs2DemoKit.Parser.EntityTracking;
using Cs2DemoKit.Parser.Models;
using SchemaNames = CS2OpenSchema.SchemaNames;

#endregion

namespace DemoViewer.NET.ViewModels;

public partial class PlaybackViewModel : ObservableObject
{
    // CS2 position field candidates, most likely first
    private static readonly string[] PositionCandidates =
    [
        SchemaNames.C_BaseEntity.GameSceneNode + "." + SchemaNames.CGameSceneNode.AbsOrigin,
        SchemaNames.CGameSceneNode.Origin,
        SchemaNames.C_BasePlayerPawn.OldOrigin,
        SchemaNames.CRagdollProp.LastOrigin
    ];

    private readonly MainViewModel _main;
    [ObservableProperty] private int _ctScore;
    [ObservableProperty] private int _currentTick;
    [ObservableProperty] private string _debugInfo = "";
    [ObservableProperty] private List<FieldSourceEntry> _fieldSources = [];

    [ObservableProperty] private List<PlayerDot> _players = [];

    [ObservableProperty] private int _roundNumber;
    [ObservableProperty] private PlayerDot? _selectedPlayer;

    // ── Navigation ────────────────────────────────────────────────────────────

    [ObservableProperty] private decimal _tickSkipCount = 1;
    [ObservableProperty] private int _tScore;

    public PlaybackViewModel(MainViewModel main)
    {
        _main = main;
        main.EntitiesRefreshed += Refresh;
        Refresh();
    }

    // Single-step frame navigation delegates
    public IRelayCommand NextFrameCommand => _main.NextFrameCommand;
    public IRelayCommand PreviousFrameCommand => _main.PreviousFrameCommand;

    public void Detach() => _main.EntitiesRefreshed -= Refresh;

    public void Refresh()
    {
        EntityTracker? tracker = _main.CurrentTracker;
        CurrentTick = _main.SelectedTickFrame?.GameTick ?? _main.SelectedFrame?.GameTick ?? 0;
        RoundNumber = 0;
        CtScore = 0;
        TScore = 0;

        if (tracker is null)
        {
            Players = [];
            DebugInfo = "No tracker — seek to a tick first";
            return;
        }

        // ── Build pawn-index → player name from CCSPlayerController entities ─────
        // Controllers link to their pawn via m_hPawn (lower 14 bits = pawn entity index).
        var pawnIndexToName = new Dictionary<int, string>();
        IReadOnlyDictionary<int, string> playerNames = _main.PlayerNames;
        foreach ((int ctrlIdx, EntityState ctrl) in tracker.CurrentEntities.AllIndexed())
        {
            if (!ctrl.ClassName.Contains("PlayerController", StringComparison.OrdinalIgnoreCase)) continue;

            // Resolve name: prefer entity field, fall back to string-table slot (slot = ctrlIdx - 1 in CS2).
            string? name = TryGetString(ctrl.Fields, SchemaNames.CCSPlayerController.SSanitizedPlayerName);
            if (string.IsNullOrEmpty(name))
                playerNames.TryGetValue(ctrlIdx - 1, out name);
            if (string.IsNullOrEmpty(name)) continue;

            // m_hPawn is a CHandle<C_BasePlayerPawn>: lower 14 bits = entity index.
            // Wire value is UInt64; TryGetHandleIndex coerces and applies the mask.
            int pawnEntityIndex = TryGetHandleIndex(ctrl.Fields, SchemaNames.CBasePlayerController.Pawn);
            if (pawnEntityIndex > 0)
                pawnIndexToName[pawnEntityIndex] = name;
        }

        List<EntityState> all = tracker.CurrentEntities.All().ToList();
        List<(int Index, EntityState Entity)> pawnMatches = tracker.CurrentEntities.AllInPvsIndexed()
            .Where(t => t.Entity.ClassName.Contains("PlayerPawn", StringComparison.OrdinalIgnoreCase))
            .ToList();
        List<PlayerDot> newPlayers = new();

        // Build a sample of what field keys exist on the first pawn (for debug display)
        string sampleFields = "";
        if (pawnMatches.Count > 0)
        {
            sampleFields = string.Join(", ",
                pawnMatches[0].Entity.Fields.Keys
                    .Where(k => k.Contains("origin", StringComparison.OrdinalIgnoreCase)
                                || k.Contains("health", StringComparison.OrdinalIgnoreCase)
                                || k.Contains("team", StringComparison.OrdinalIgnoreCase)
                                || k.Contains("life", StringComparison.OrdinalIgnoreCase))
                    .Take(8));
            if (string.IsNullOrEmpty(sampleFields))
                sampleFields = string.Join(", ", pawnMatches[0].Entity.Fields.Keys.Take(6));
        }

        foreach ((int pawnIdx, EntityState entity) in pawnMatches)
        {
            (Vector3? pos, string posFieldName) = FindPosition(entity.Fields);
            if (pos is null) continue;

            int health = TryGetInt(entity.Fields, SchemaNames.CBaseEntity.Health);
            // LIFE_ALIVE = 0; any non-zero state is dead/respawning.
            int lifeState = TryGetInt(entity.Fields, SchemaNames.CBaseEntity.LifeState);
            bool alive = entity.Fields.ContainsKey(SchemaNames.CBaseEntity.LifeState)
                ? lifeState == 0
                : health > 0;
            int team = TryGetInt(entity.Fields, SchemaNames.CBaseEntity.TeamNum);

            pawnIndexToName.TryGetValue(pawnIdx, out string? playerName);
            string displayName = playerName ?? $"#{entity.Serial}";

            List<FieldSourceEntry> sources = new()
            {
                new FieldSourceEntry
                {
                    EntityClass = entity.ClassName,
                    EntitySerial = entity.Serial,
                    FieldName = posFieldName,
                    Value = $"({pos.Value.X:F1}, {pos.Value.Y:F1}, {pos.Value.Z:F1})"
                },
                new FieldSourceEntry
                {
                    EntityClass = entity.ClassName,
                    EntitySerial = entity.Serial,
                    FieldName = SchemaNames.CBaseEntity.Health,
                    Value = health.ToString(CultureInfo.InvariantCulture)
                },
                new FieldSourceEntry
                {
                    EntityClass = entity.ClassName,
                    EntitySerial = entity.Serial,
                    FieldName = SchemaNames.CBaseEntity.LifeState,
                    Value = alive ? "alive (0)" : $"dead ({lifeState})"
                },
                new FieldSourceEntry
                {
                    EntityClass = entity.ClassName,
                    EntitySerial = entity.Serial,
                    FieldName = SchemaNames.CBaseEntity.TeamNum,
                    Value = $"{team}  ({(team == 3 ? "CT" : team == 2 ? "T" : "?")})"
                }
            };

            newPlayers.Add(new PlayerDot
            {
                ClassName = entity.ClassName,
                Serial = entity.Serial,
                WorldPos = pos.Value,
                Health = health,
                IsAlive = alive,
                TeamNum = team,
                DisplayName = displayName,
                FieldSources = sources
            });
        }

        // Game rules and team scores
        foreach (EntityState entity in all)
        {
            if (entity.ClassName == "CCSGameRules")
            {
                if (entity.Fields.ContainsKey(SchemaNames.CCSGameRules.TotalRoundsPlayed))
                    RoundNumber = TryGetInt(entity.Fields, SchemaNames.CCSGameRules.TotalRoundsPlayed) + 1;
            }
            else if (entity.ClassName == "CCSTeam")
            {
                int teamNum = TryGetInt(entity.Fields, SchemaNames.CBaseEntity.TeamNum);
                // CS2 doesn't network a total score field — it tracks halves separately. Sum them.
                int score = TryGetInt(entity.Fields, SchemaNames.CCSTeam.ScoreFirstHalf)
                          + TryGetInt(entity.Fields, SchemaNames.CCSTeam.ScoreSecondHalf)
                          + TryGetInt(entity.Fields, SchemaNames.CCSTeam.ScoreOvertime);
                if (teamNum == 3) CtScore = score;
                else if (teamNum == 2) TScore = score;
            }
        }

        Players = newPlayers;

        // Debug line: entities found, pawns found, whether position was resolved, sample keys
        DebugInfo = newPlayers.Count > 0
            ? $"{all.Count} entities  •  {pawnMatches.Count} pawns  •  {newPlayers.Count} with pos  •  {pawnIndexToName.Count} named"
            : pawnMatches.Count > 0
                ? $"{all.Count} entities  •  {pawnMatches.Count} pawns found but no position — keys: {sampleFields}"
                : $"{all.Count} entities  •  0 pawns (classes: {string.Join(", ", all.Select(e => e.ClassName).Distinct().Take(6))})";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (Vector3? pos, string fieldName) FindPosition(IReadOnlyDictionary<string, object?> fields)
    {
        // Try known candidates first
        foreach (string key in PositionCandidates)
            if (fields.TryGetValue(key, out object? v) && v is Vector3 vec)
                return (vec, key);

        // Fallback: any Vector3 with "origin" in the key
        foreach (KeyValuePair<string, object?> kv in fields)
            if (kv.Value is Vector3 fallback && kv.Key.Contains("origin", StringComparison.OrdinalIgnoreCase))
                return (fallback, kv.Key);

        // Last resort: first Vector3 field on the entity
        foreach (KeyValuePair<string, object?> kv in fields)
            if (kv.Value is Vector3 any)
                return (any, kv.Key);

        return (null, "");
    }

    partial void OnSelectedPlayerChanged(PlayerDot? value)
        => FieldSources = value?.FieldSources.ToList() ?? [];

    [RelayCommand]
    private void SkipBack()
    {
        int n = Math.Max(1, (int)TickSkipCount);
        ObservableCollection<TickGroup> groups = _main.TickGroups;
        TickGroup? current = _main.SelectedTickGroup;
        int idx = current is null ? 0 : groups.IndexOf(current);
        int target = Math.Max(idx - n, 0);
        if (target >= 0 && target < groups.Count)
            _main.SelectedTickGroup = groups[target];
    }

    [RelayCommand]
    private void SkipForward()
    {
        int n = Math.Max(1, (int)TickSkipCount);
        ObservableCollection<TickGroup> groups = _main.TickGroups;
        TickGroup? current = _main.SelectedTickGroup;
        int idx = current is null ? -1 : groups.IndexOf(current);
        int target = Math.Min(idx + n, groups.Count - 1);
        if (target >= 0 && target < groups.Count)
            _main.SelectedTickGroup = groups[target];
    }

    // Networked fields arrive as a variety of integer widths — most notably entity
    // handles are UInt64. The narrow `is uint` cast that was previously used would
    // silently fail on UInt64, leaving handles unresolved. These helpers coerce
    // across all integral widths.
    private static int TryGetInt(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out object? v)) return 0;
        return v switch
        {
            int i    => i,
            uint u   => unchecked((int)u),
            long l   => unchecked((int)l),
            ulong u  => unchecked((int)u),
            short s  => s,
            ushort u => u,
            byte b   => b,
            sbyte s  => s,
            _        => 0,
        };
    }

    private static string? TryGetString(IReadOnlyDictionary<string, object?> fields, string key)
        => fields.TryGetValue(key, out object? v) && v is string s && s.Length > 0 ? s : null;

    // CS2 entity-handle decoder: lower 14 bits = entity index. Wire values arrive
    // as UInt64 (most common) or other integer widths.
    private static int TryGetHandleIndex(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out object? v)) return 0;
        uint handle = v switch
        {
            uint u   => u,
            int i    => unchecked((uint)i),
            long l   => unchecked((uint)l),
            ulong u  => unchecked((uint)u),
            short s  => unchecked((uint)s),
            ushort u => u,
            byte b   => b,
            sbyte s  => unchecked((uint)s),
            _        => 0u,
        };
        if (handle == 0 || handle == 0xFFFF_FFFF) return 0;
        return (int)(handle & 0x3FFF);
    }
}
