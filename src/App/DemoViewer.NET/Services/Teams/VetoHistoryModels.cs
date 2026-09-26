#region

using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>Whether a veto step took a map out of the pool, or left it as the pick.</summary>
public enum VetoAction
{
    Ban,
    Pick
}

/// <summary>
///     One veto step the user entered by hand against an opponent (plan.md D5: manual entry only, in
///     Phase 5; no scraping — the artifact's veto model needs match-series metadata no demo carries,
///     F12). <see cref="Order" /> is the pick order the user typed the step in, one-based.
/// </summary>
public sealed class VetoEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OpponentTeamId { get; set; }

    public int Order { get; set; }

    public string Map { get; set; } = "";

    public VetoAction Action { get; set; } = VetoAction.Ban;

    /// <summary>True when the opponent this history is filed under made the step; false for us.</summary>
    public bool ByOpponent { get; set; }

    public string Note { get; set; } = "";
}

/// <summary>
///     <c>&lt;config&gt;/veto-history.json</c>, beside <c>teams.json</c> (F12, D5): the user's own manual
///     record of veto steps, the model's only substitute for match-series metadata no demo carries.
///     Refused rather than overwritten when it cannot be read, the same rule <c>teams.json</c> follows.
/// </summary>
public sealed class VetoHistoryFile
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;

    public List<VetoEntry> Entries { get; set; } = [];

    /// <summary>Same shape as <c>TeamsFile.JsonOptions</c>: camel case, enums by name, indented.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
