#region

using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Visualization;

#endregion

namespace DemoViewer.NET.Debugging;

/// <summary>Whether a graph breakpoint is armed on a node or an edge.</summary>
public enum GraphBreakpointTarget
{
    /// <summary>Breaks on the rising edge of a node's condition (default: the node becoming active).</summary>
    Node,

    /// <summary>Breaks each time an edge fires (default), optionally narrowed by an event condition.</summary>
    Edge
}

/// <summary>
///     A user-set breakpoint on the Analysis-engine graph. Unlike the entity-tracker
///     <see cref="Breakpoint" /> (frame-indexed, closed-kind, session-only), graph breakpoints are
///     <em>message</em>-indexed, expression-conditioned, and persisted per-demo. They stop the
///     message-seek timeline at the indices where their condition holds.
///     <para>
///         <b>Identity</b> mirrors the visualization hit-test keys: a node breakpoint is keyed by
///         <see cref="NodeName" />; an edge breakpoint by (<see cref="EdgeSource" />,
///         <see cref="EdgeDest" />, <see cref="EdgeLabel" />). These are the persisted fields. The
///         hit indices (<see cref="HitIndices" />) are transient — recomputed from the evaluation,
///         never serialized.
///     </para>
/// </summary>
public sealed partial class GraphBreakpoint : ObservableObject
{
    private static int _nextId;

    /// <summary>
    ///     True while an entity-read edge condition's hits are being computed off-thread (the lazy entity
    ///     replay). The breakpoint list shows "computing…" rather than a misleading "0 hits" until the
    ///     async build hands back. Transient — never persisted.
    /// </summary>
    [ObservableProperty]
    private bool _computing;

    /// <summary>
    ///     The rule-expression condition. <c>null</c> means "use the default" — a node breaks on
    ///     becoming active; an edge breaks every time it fires.
    /// </summary>
    [ObservableProperty]
    private string? _condition;

    /// <summary>Disabled breakpoints stay in the list and keep their marker, but never halt seek.</summary>
    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>Times the seek timeline halted on this breakpoint this session.</summary>
    [ObservableProperty]
    private int _hitCount;

    private IReadOnlyList<int> _hitIndices = [];

    /// <summary>Stable id for UI list selection and removal.</summary>
    public int Id { get; } = Interlocked.Increment(ref _nextId);

    /// <summary>Whether this breakpoint targets a node or an edge.</summary>
    public required GraphBreakpointTarget TargetKind { get; init; }

    /// <summary>Node name — set for <see cref="GraphBreakpointTarget.Node" />, else <c>null</c>.</summary>
    public string? NodeName { get; init; }

    /// <summary>Edge source-node name — set for <see cref="GraphBreakpointTarget.Edge" />.</summary>
    public string? EdgeSource { get; init; }

    /// <summary>Edge destination-node name — set for <see cref="GraphBreakpointTarget.Edge" />.</summary>
    public string? EdgeDest { get; init; }

    /// <summary>Edge label — set for <see cref="GraphBreakpointTarget.Edge" /> (the event name).</summary>
    public string? EdgeLabel { get; init; }

    /// <summary>
    ///     Edge condition label — set for <see cref="GraphBreakpointTarget.Edge" />. Part of the edge
    ///     identity: one rule can wire two triggers on the same event between the same node pair,
    ///     differing only by condition (e.g. foe vs friend), which render as parallel edges with the
    ///     same <see cref="EdgeLabel" />. Without this they'd collapse to one breakpoint tracking the
    ///     wrong fire set. <c>null</c> for an unconditional edge.
    /// </summary>
    public string? EdgeConditionLabel { get; init; }

    /// <summary>
    ///     Sorted global message indices at which this breakpoint's condition holds, recomputed from
    ///     the evaluation snapshots after every load and on every edit. Transient — never persisted.
    ///     Assigning raises <see cref="MatchCount" /> so a bound breakpoint list updates live.
    /// </summary>
    public IReadOnlyList<int> HitIndices
    {
        get => _hitIndices;
        set
        {
            _hitIndices = value;
            OnPropertyChanged(nameof(MatchCount));
        }
    }

    /// <summary>
    ///     How many messages this breakpoint's condition matches in the current demo (the count of
    ///     <see cref="HitIndices" />) — the always-visible "stops N×" figure in the breakpoint list.
    ///     Distinct from <see cref="HitCount" />, which counts session halts.
    /// </summary>
    public int MatchCount => _hitIndices.Count;

    /// <summary>One-line label for breakpoint lists.</summary>
    public string DisplayText
    {
        get
        {
            string edgeTag = $"{EdgeSource}→{EdgeDest}"
                             + (string.IsNullOrEmpty(EdgeLabel) ? "" : $" [{EdgeLabel}]")
                             + (string.IsNullOrEmpty(EdgeConditionLabel) ? "" : $" ⟨{EdgeConditionLabel}⟩");
            string target = TargetKind == GraphBreakpointTarget.Node ? NodeName ?? "(node)" : edgeTag;
            return string.IsNullOrWhiteSpace(Condition) ? target : $"{target}  ⟨{Condition}⟩";
        }
    }

    /// <summary>True when this breakpoint targets the given node (by name).</summary>
    public bool Matches(IGraphNode node) =>
        TargetKind == GraphBreakpointTarget.Node && NodeName == node.Name;

    /// <summary>True when this breakpoint targets the given edge (by source/dest/label/condition).</summary>
    public bool Matches(IGraphEdge edge) =>
        TargetKind == GraphBreakpointTarget.Edge
        && EdgeSource == edge.Source.Name
        && EdgeDest == edge.Destination.Name
        && EdgeLabel == edge.Label
        && EdgeConditionLabel == edge.ConditionLabel;
}
