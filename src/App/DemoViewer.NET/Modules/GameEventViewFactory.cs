#region

using System.Globalization;
using DemoViewer.NET.Modules.Abstractions;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     Projects a frame's decoded game events into the read-only <see cref="GameEventView" /> surface that
///     modules consume. Each event is enriched with its decoded fields — de-stringified from the parser's
///     <see cref="GameEvent.GetDecodedFields" /> (name, stringified value, wire-type) back into typed boxed
///     values keyed by field name — so an event-driven module (e.g. the 2D kill feed) can read
///     <c>Attacker</c> / <c>UserId</c> / <c>Weapon</c> / <c>Headshot</c> without taking a dependency on the
///     SDK's typed event records (the abstractions assembly stays Parser-free). Field names are the
///     SDK payload record's property names; the catalog embedded in CS2DemoKit.Analysis (<c>CatalogResource.Load()</c>) is the authoritative spelling.
/// </summary>
internal static class GameEventViewFactory
{
    private static readonly IReadOnlyDictionary<string, object?> _emptyFields =
        new Dictionary<string, object?>();

    /// <summary>
    ///     Projects a single decoded event to an enriched <see cref="GameEventView" /> (Name + GameTick +
    ///     typed Fields). Used to pre-build a whole-demo event timeline a module filters by tick window
    ///     (the kill feed) via <c>IModuleContext.GetEventTimeline</c>.
    /// </summary>
    public static GameEventView FromEvent(GameEvent e) => new()
    {
        Name = e.Name,
        // GameTick (NOT ServerTick): CS2 delivers a player_death message in a LATER demo frame than the
        // tick it fired, so ServerTick is the delivery tick (= game tick + ServerStartTick) while GameTick is
        // the true firing tick — the SAME clock as the playhead (DemoFrame.ServerTick / CurrentTick is the
        // game tick in CS2). Stamping with ServerTick made the kill feed appear ServerStartTick ticks late.
        Tick = e.GameTick,
        Fields = ToFields(e)
    };

    // De-stringify GetDecodedFields() — (Name, Value, WireType) — into typed boxed values keyed by Name.
    // String values arrive quoted (F wraps them as "\"value\""); strip the wrapping quotes. A parse failure
    // falls back to the raw string rather than throwing (a malformed field never breaks a module).
    //
    // The wire type is the SDK's KV1 type tag, a 13-value vocabulary — NOT the five names the retired
    // generator emitted. `int`/`bool`/`float`/`uint64`/`string` were the whole vocabulary before, and every
    // richer tag (a `short` damage count, a `player_controller_and_pawn` slot) fell through to `_ => value`
    // and reached modules as a raw string. Silent: the reads are all `v is int i ? i : 0`, so a mistyped
    // field reads as zero rather than throwing. Every tag that survives materialisation is mapped below.
    //
    // An unrecognised tag still falls through to the raw string, deliberately. Sniffing the value's shape
    // would turn a future SDK tag from a visible wrong-type into an invisible plausible one — `local` renders
    // as "System.Byte[]" and would sniff clean as a string.
    internal static IReadOnlyDictionary<string, object?> ToFields(GameEvent e)
    {
        IReadOnlyList<(string Name, string Value, string WireType)> decoded = e.GetDecodedFields();
        if (decoded.Count == 0)
        {
            return _emptyFields;
        }

        // Case-insensitive, matching how the rules layer resolves `event.<Field>`. The SDK's identifier
        // casing is upstream's to change (`AttackerInAir` was `Attackerinair` one major ago), and a module
        // keyed on the old casing should read the field, not silently miss it.
        Dictionary<string, object?> fields = new(decoded.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value, string wireType) in decoded)
        {
            fields[name] = wireType switch
            {
                "bool" => string.Equals(value, "True", StringComparison.OrdinalIgnoreCase),
                "string" => Unquote(value),

                // Byte / Int16 / Int32 once materialised — all fit an int.
                "byte" or "short" or "int" or "long" or "player_controller" =>
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)
                        ? i
                        : value,

                // A player_pawn field's sole wire key is `<name>_pawn`, a pawn entity handle —
                // uint like `ehandle`, and the no-player sentinel 0xFFFFFFFF does not fit an int.
                // player_controller_and_pawn tags BOTH of its wire keys' properties; the declared
                // name is the controller slot (int), the `<Name>Pawn` companion the pawn handle
                // (uint). Tag + suffix per SDK docs/MIGRATION-4.1.md.
                "player_pawn" =>
                    uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint p)
                        ? p
                        : value,
                "player_controller_and_pawn" => name.EndsWith("Pawn", StringComparison.Ordinal)
                    ? uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint cp)
                        ? cp
                        : value
                    : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cs)
                        ? cs
                        : value,

                "float" => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
                    ? f
                    : value,
                "ehandle" => uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint h)
                    ? h
                    : value,
                "uint64" => ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong u)
                    ? u
                    : value,
                _ => value
            };
        }

        return fields;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
}
