namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The pack's own processing-queue job kinds. <see cref="StratBookPack.JobKinds" /> declares them; jobs name
///     them by id. Every job carries the pack's id, so switching the pack off cancels them all.
/// </summary>
internal static class StratBookJobKinds
{
    public const string Mining = "stratbook.mining";
    public const string Preview = "stratbook.preview";
    public const string LineupClips = "stratbook.lineup-clips";
    public const string SuggestionsInbox = "stratbook.suggestions";
    public const string Teams = "stratbook.teams";
    public const string Tuning = "stratbook.tuning";

    public static readonly ExtensionJobKind[] All =
    [
        new(Mining, "mining", false, 2),
        new(Preview, "preview", false, 2),
        new(LineupClips, "clips", false, 3),
        new(SuggestionsInbox, "suggestions", false, 2),
        new(Teams, "teams", true),
        new(Tuning, "tuning", false, 2)
    ];
}
