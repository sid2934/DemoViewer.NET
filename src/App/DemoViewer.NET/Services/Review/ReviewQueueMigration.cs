#region

using System.Text.Json;
using System.Text.RegularExpressions;

#endregion

namespace DemoViewer.NET.Services.Review;

/// <summary>What <see cref="ReviewQueueMigration.DropGeneratedLineupClips" /> did.</summary>
/// <param name="Dropped">Generated lineup clips taken out.</param>
/// <param name="Kept">Lineup clips kept because someone wrote on them.</param>
/// <param name="CardsDropped">"Lineup clips, &lt;map&gt;" title cards left empty and taken out.</param>
/// <param name="Written">False when the rewrite did not land; the queue writes the migrated set on its next save.</param>
public sealed record ReviewQueueMigrationResult(int Dropped, int Kept, int CardsDropped, bool Written = true);

/// <summary>
///     Lineup clips left the Review Queue: the Utility Book's position card shows them. Earlier builds queued
///     one per lineup under "Lineup clips, &lt;map&gt;" cards. This drops the ones nobody touched and rewrites
///     the file once, verified before it replaces the old one. Idempotent: a second pass finds nothing.
/// </summary>
public static partial class ReviewQueueMigration
{
    /// <summary>The copy of the file as it was before the rewrite, beside it.</summary>
    public const string BackupFileName = "review-queue.lineups.bak";

    private const string LineupSource = "lineup";
    private const string LineupCardPrefix = "Lineup clips, ";

    /// <summary>
    ///     True for a clip exactly as the lineup service wrote it: lineup source, no question, and the note
    ///     it generated ("&lt;title&gt;. setpos x y z; setang p y r").
    /// </summary>
    /// <param name="entry">A queue entry.</param>
    public static bool IsGeneratedLineupClip(ReviewEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Kind == ReviewEntryKind.Clip
               && string.Equals(entry.Source, LineupSource, StringComparison.Ordinal)
               && entry.Question.Length == 0
               && !entry.Reviewed
               && GeneratedNote().IsMatch(entry.Note);
    }

    /// <summary>The entries without the generated lineup clips and without lineup cards left empty.</summary>
    /// <param name="entries">The queue in order.</param>
    /// <param name="result">What was dropped and kept.</param>
    public static List<ReviewEntry> DropGeneratedLineupClips(IReadOnlyList<ReviewEntry> entries, out ReviewQueueMigrationResult result)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<ReviewEntry> kept = new(entries.Count);
        int dropped = 0, keptLineups = 0;
        bool underLineupCard = false;
        foreach (ReviewEntry entry in entries)
        {
            if (entry.Kind == ReviewEntryKind.Section)
            {
                underLineupCard = entry.Title.StartsWith(LineupCardPrefix, StringComparison.Ordinal);
            }

            // A clip the user moved under another card, or whose card they renamed, is theirs now.
            if (underLineupCard && IsGeneratedLineupClip(entry))
            {
                dropped++;
                continue;
            }

            if (entry.Kind == ReviewEntryKind.Clip && string.Equals(entry.Source, LineupSource, StringComparison.Ordinal))
            {
                keptLineups++;
            }

            kept.Add(entry);
        }

        int cards = 0;
        for (int i = kept.Count - 1; i >= 0; i--)
        {
            bool empty = i + 1 >= kept.Count || kept[i + 1].Kind == ReviewEntryKind.Section;
            if (kept[i].Kind == ReviewEntryKind.Section && empty
                                                        && kept[i].Title.StartsWith(LineupCardPrefix, StringComparison.Ordinal))
            {
                kept.RemoveAt(i);
                cards++;
            }
        }

        result = new ReviewQueueMigrationResult(dropped, keptLineups, dropped == 0 ? 0 : cards);
        return dropped == 0 ? [.. entries] : kept;
    }

    /// <summary>
    ///     Rewrites <paramref name="path" /> without the generated lineup clips. The old file is copied to
    ///     <see cref="BackupFileName" /> first; the new one is written beside it, read back and compared, and
    ///     only then moved over it. Without a backup nothing changes: the file's own entries come back and
    ///     the next load tries again. Once the backup exists the migrated entries come back whatever happens
    ///     after, and <see cref="ReviewQueueMigrationResult.Written" /> says whether the caller still has to
    ///     write them; the unmigrated set is never handed back to be saved over a migrated file.
    /// </summary>
    /// <param name="path">The queue file.</param>
    /// <param name="file">What was read from it.</param>
    /// <param name="result">What was dropped, or null when nothing was or no backup could be taken.</param>
    public static IReadOnlyList<ReviewEntry> MigrateFile(string path, ReviewQueueFile file, out ReviewQueueMigrationResult? result)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(file);
        result = null;
        List<ReviewEntry> kept = DropGeneratedLineupClips(file.Entries, out ReviewQueueMigrationResult tally);
        if (tally.Dropped == 0)
        {
            return file.Entries;
        }

        try
        {
            File.Copy(path, Path.Combine(Path.GetDirectoryName(path) ?? "", BackupFileName), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return file.Entries;
        }

        string next = path + ".migrating";
        try
        {
            ReviewQueueFile rewritten = new() { SchemaVersion = file.SchemaVersion, Clock = file.Clock, Entries = kept, Extra = file.Extra };
            File.WriteAllText(next, JsonSerializer.Serialize(rewritten, ReviewQueueFile.JsonOptions));
            ReviewQueueFile? back = JsonSerializer.Deserialize<ReviewQueueFile>(File.ReadAllText(next), ReviewQueueFile.JsonOptions);
            if (back is null || !back.Entries.Select(e => e.Id).SequenceEqual(kept.Select(e => e.Id)))
            {
                File.Delete(next);
                result = tally with { Written = false };
                return kept;
            }

            File.Move(next, path, true);
            result = tally;
            return kept;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            try
            {
                File.Delete(next);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Left behind; overwritten by the next attempt.
            }

            result = tally with { Written = false };
            return kept;
        }
    }

    [GeneratedRegex(@"^.+\. setpos -?\d+\.\d+ -?\d+\.\d+ -?\d+\.\d+; setang -?\d+\.\d+ -?\d+\.\d+ -?\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedNote();
}
