#region

using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     What the user did to one team's Dossier (plan.md §3, Dossier Editing And Export): the findings
///     starred for the one-pager, the generated lines rewritten or left out, the free notes added beside
///     them, and the summary that heads the export. A finding is addressed by its key (the section and
///     what it counts, never an index), so a star or an edit survives a rebuild that reorders or adds
///     findings; one whose finding no longer exists is kept and simply not shown.
/// </summary>
public sealed class DossierTeamNotes
{
    public Guid TeamId { get; set; }

    /// <summary>The user's own paragraph at the top of both forms; empty prints nothing.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Keys of the findings on the one-pager.</summary>
    public List<string> Starred { get; set; } = [];

    /// <summary>A finding's rewritten text by key. Absent means the generated line stands.</summary>
    public Dictionary<string, string> Edits { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keys of generated findings left out of both forms.</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>Findings the user wrote, in the order added.</summary>
    public List<DossierNote> Notes { get; set; } = [];
}

/// <summary>One finding the user wrote by hand. Its key is <c>note:</c> plus <see cref="Id" />.</summary>
public sealed class DossierNote
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Text { get; set; } = "";
}

/// <summary>
///     <c>&lt;config&gt;/dossier-notes.json</c>, beside <c>teams.json</c> and <c>veto-history.json</c>.
///     Refused rather than overwritten when it cannot be read, the same rule both follow.
/// </summary>
public sealed class DossierNotesFile
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;

    public List<DossierTeamNotes> Teams { get; set; } = [];

    /// <summary>Same shape as <see cref="VetoHistoryFile.JsonOptions" />.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
