# Demo Source Profiles

DemoViewer.NET parses CS2 demos from multiple sources — Valve matchmaking
GOTV, HLTV/pro broadcast recordings, FACEIT, POV — that share the same
container format but emit subtly different game-event vocabularies. The
`DemoSourceProfile` abstraction lets a single rule set work across all of
them without source-specific branches in the engine or rule files.

## How it works

A profile is a comprehensive enumeration of CS2 logical events as virtual
properties on the abstract base class `DemoSourceProfile`. Each accessor
returns a `LogicalEventBinding?` — an ordered list of concrete event names
the source emits for that logical concept. `null` means the source does
not emit any event for the concept (capability advertising in disguise).

```
                ┌──────────────────────┐
   demo header ─▶│ DemoSourceClassifier │── DemoProfile (Kind, BuildNumber)
                └──────────────────────┘
                          │
                          ▼
        ┌──────────────────────────────────┐
        │ DemoSourceProfileRegistry.Resolve │── tightest matching profile
        └──────────────────────────────────┘
                          │
                          ▼
        ┌──────────────────────────────────┐
        │ LogicalEventResolver(profile)    │── $logical → concrete events
        └──────────────────────────────────┘
                          │
                          ▼
        ┌──────────────────────────────────┐
        │ RuleChainBuilder                 │── expands triggers, gates by
        │                                  │   `requires:` at build time
        └──────────────────────────────────┘
```

Profile selection is auto-detected from the demo header (ClientName
contains "HLTV", "SourceTV", etc.) and can be overridden via
`DemoParser.Parse(bytes, profileOverride)`. The active profile is fixed
at evaluator-build time; runtime dispatch is unchanged from a
single-source build.

## Built-in profiles

| Profile | Kind | Notes |
|---|---|---|
| `Cs2GotvProfile` | `GotvMatchmaking` | Default fallback. Comprehensive event vocabulary. |
| `Cs2HltvProfile` | `HltvPro` | Inherits GOTV, overrides for pro broadcast recordings: `cs_pre_restart` for round-end, `RoundOfficiallyEnded => null`, `player_blind`/`weapon_reload`/`weapon_zoom`/`player_jump`/`player_footstep` absent, `grenade_thrown` added. |
| `Cs2FaceitProfile` | `Faceit` | Pure skeleton — inherits GOTV with only `Kind` overridden. Will diverge as FACEIT-specific differences surface. |
| `Cs2PovProfile` | `Pov` | Pure skeleton — inherits GOTV with only `Kind` overridden. POV (player-recorded) demos lack server-side events; overrides will be added when POV demos are bench-tested. |

The full list of logical-event accessors lives on `DemoSourceProfile` (`src/Analysis/DemoViewer.NET.Analysis.Abstractions/DemoSourceProfile.cs`). For empirical data on which CS2 events actually fire across demo sources, see [`Demo-Event-Compatibility.md`](./Demo-Event-Compatibility.md).

## Writing rules with logical events

Trigger references prefixed with `$` are logical-event references:

```yaml
- id: rounds_played
  type: counter
  triggers:
    - on: $round_freeze_end       # logical event
      action: increment
```

The rule builder expands `$round_freeze_end` to whatever concrete event
the active profile binds it to. On GOTV that is `round_freeze_end`; on
a future profile it could be a different event entirely. Concrete event
names (`on: round_freeze_end`) continue to work unchanged.

### Multi-event bindings

A logical event may bind to several concrete events with `FirstWins`
semantics — the first to fire per round wins, fallbacks suppressed.
The only multi-event binding today is `$round_end`:

| Logical | GOTV binding | HLTV binding |
|---|---|---|
| `$round_end` | `round_officially_ended` → `cs_win_panel_match` | `cs_pre_restart` → `cs_win_panel_match` |

Because GOTV's final round lacks `round_officially_ended`, the chain
falls through to `cs_win_panel_match` — Valve's match-summary marker,
which fires for any normally-completed match. Demos cut off mid-round
(no `cs_win_panel_match` ever fires) will not finalise their final
round; that is an accepted edge case in v0.0.2.

`$match_end` is currently a single-event binding (`cs_win_panel_match`
on both GOTV and HLTV) and does not need first-wins suppression.

**First-wins-per-round suppression is implemented.** When a multi-event
`$logical` reference is combined with a non-idempotent action
(`increment`, `set`), the rule builder allocates a per-rule per-trigger
`__seen_<rule.Id>_<triggerIndex>` round-scoped bool guard. The guard
checks ahead of the edge's effect; on a successful fire it activates,
suppressing the remaining concrete fallbacks within the same round.
The guard auto-resets at round boundaries via the
`RoundScopedLogicNodeReset` adapter. Bool `activate`/`deactivate`
triggers remain unaffected — they are idempotent and don't allocate a
guard.

### Capability gating with `requires:`

A rule that strictly depends on a logical event the active profile may
lack should declare `requires:`:

```yaml
- id: enemies_flashed
  type: counter
  requires: [player_blind]
  triggers:
    - on: $player_blind
      condition: "event.AttackerSlot == player.slot"
      action: increment
```

When the active profile does not bind `player_blind` (HLTV demos lack
the underlying event), the rule is silently skipped at build time —
its node and edges are never created. Without `requires:`, an
unguarded `$logical` reference that resolves to `null` raises a build
error citing the rule id and the active profile's `DisplayName`.

`requires:` entries are snake_case logical names matching the property
names on `DemoSourceProfile` converted from PascalCase
(`RoundOfficiallyEnded` → `round_officially_ended`,
`HeGrenadeDetonate` → `he_grenade_detonate`).

## Terminal events — no synthetic injection

The evaluator does **not** synthesise any terminal event at end-of-demo.
Final-round finalisation relies entirely on real concrete events from
the demo stream: `cs_win_panel_match` for GOTV/HLTV completed matches,
or `cs_pre_restart` for the per-round path on HLTV. Round-end edges
(rules + infra: `RoundEndEnrichmentEdge`,
`ClutchResolutionEnrichmentEdge`, `ThresholdTallyEdge`,
`ComputeOnRoundEndEdge`) subscribe to every concrete event in the
active profile's `$round_end` binding, with first-wins suppression
guarding non-idempotent effects so multiple events in the same round
don't double-fire.

This was a deliberate simplification — earlier iterations injected a
synthetic `EndOfDemoEvent` (and before that a synthetic
`RoundOfficiallyEndedEvent`) to paper over the missing-event problem,
but multi-event logical bindings cover the same ground without the
fragility of a synthetic frame appearing outside the demo's natural
event stream.

## Adding a custom profile (future work)

The `DemoSourceProfileRegistry.Register(DemoSourceProfile)` extension
point is reserved for a future custom-DLL loader. v0.0.2 only uses
internally-shipped profiles. Authors writing rules for unusual sources
can extend `DemoSourceProfile` directly today — the abstraction is
stable; adding new logical events to the base class is non-breaking
because all subclasses inherit the default `null`.

## Known HLTV gaps

Real HLTV-format demo bench testing is deferred — no representative
sample is checked into the project yet. Pro-broadcast demos in
`demos/pro-demos/` classify as `GotvMatchmaking` because their
`ClientName` is "SourceTV Demo" (ESL/PGL tournament servers run their
own GOTV recordings, not Valve HLTV). Specific items the HLTV smoke
test should cover when a sample becomes available:

- Confirm classifier picks `Cs2HltvProfile` for an HLTV-format demo.
- Confirm the evaluator runs without crashing.
- Confirm `requires: [player_blind]` rules silently skip on HLTV.
- Verify `survived` and the round-end counters in
  `rules/player-stats.yaml` (now on `$round_end`) activate correctly on
  `cs_pre_restart` — the HLTV binding for `$round_end`. Behavioral
  change vs. v0.0.1: `survived` previously skipped on HLTV, now it
  fires on first `$round_end` event. Untested timing on HLTV; verify
  `alive` parent gate is still in the right state when
  `cs_pre_restart` arrives.
- Verify per-stat accuracy on a Leetify-cross-checkable HLTV match.
- Verify the four round-end infrastructure edges
  (`RoundEndEnrichmentEdge`, `ClutchResolutionEnrichmentEdge`,
  `ThresholdTallyEdge`, `ComputeOnRoundEndEdge`) fire correctly on
  HLTV. They subscribe to every concrete event in the active profile's
  `$round_end` binding (`cs_pre_restart` per-round; `cs_win_panel_match`
  as the match-end fallback). The non-idempotent `ThresholdTallyEdge`
  uses a per-player first-wins guard.
