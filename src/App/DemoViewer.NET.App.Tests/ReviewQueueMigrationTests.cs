#region

using System.Text.Json;
using DemoViewer.NET.Services.Review;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Lineup clips left the Review Queue: the load drops the ones the lineup service generated and nobody
///     touched, keeps the rest, and rewrites the file once, verified, with the old one backed up.
/// </summary>
public class ReviewQueueMigrationTests
{
    private const string ConsoleLine = "setpos -613.43 615.90 -78.98; setang -51.19 -169.10 0.00";

    private static ReviewEntry Lineup(string note) =>
        ReviewEntry.Clip("/d/a.dem", 100, 200, note, ReviewSources.Lineup, 64) with { LineupId = Guid.NewGuid() };

    private static string Root() => Directory.CreateTempSubdirectory("dv-review-migration-").FullName;

    private static void Write(string root, IEnumerable<ReviewEntry> entries) =>
        File.WriteAllText(Path.Combine(root, ReviewQueue.FileName),
            JsonSerializer.Serialize(new ReviewQueueFile { Entries = [.. entries] }, ReviewQueueFile.JsonOptions));

    [Test]
    public async Task TheLoad_DropsUntouchedLineupClips_KeepsWhatAUserWroteOn_AndBacksUpTheOldFile()
    {
        string root = Root();
        try
        {
            ReviewEntry asked = Lineup("Flash into B. " + ConsoleLine) with { Question = "who pops it?" };
            ReviewEntry renoted = Lineup("my favourite B flash");
            ReviewEntry dossier = ReviewEntry.Clip("/d/x.dem", 1, 2, "plant", ReviewSources.Dossier);
            Write(root,
            [
                ReviewEntry.Section("Lineup clips, de_mirage"), Lineup("Smoke into CT. " + ConsoleLine), Lineup("Molotov into Jungle. " + ConsoleLine),
                ReviewEntry.Section("Lineup clips, de_nuke"), Lineup("Smoke into Outside. " + ConsoleLine), asked, renoted,
                ReviewEntry.Section("Dossier"), dossier
            ]);
            string original = await File.ReadAllTextAsync(Path.Combine(root, ReviewQueue.FileName));

            ReviewQueue queue = new(root);
            await queue.Loaded;

            using (Assert.Multiple())
            {
                await Assert.That(queue.Migration).IsEqualTo(new ReviewQueueMigrationResult(3, 2, 1));
                await Assert.That(queue.Entries.Select(e => e.Kind == ReviewEntryKind.Section ? e.Title : e.Note))
                    .IsEquivalentTo(["Lineup clips, de_nuke", asked.Note, renoted.Note, "Dossier", "plant"]);
                await Assert.That(await File.ReadAllTextAsync(Path.Combine(root, ReviewQueueMigration.BackupFileName)))
                    .IsEqualTo(original);
                await Assert.That(File.Exists(Path.Combine(root, ReviewQueue.FileName + ".migrating"))).IsFalse();
            }

            ReviewQueue again = new(root);
            await again.Loaded;
            using (Assert.Multiple())
            {
                await Assert.That(again.Migration).IsNull().Because("a second load finds nothing to drop");
                await Assert.That(again.Entries.Count).IsEqualTo(5);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ARefusedFile_IsNotMigrated()
    {
        string root = Root();
        try
        {
            string newer = """{ "schemaVersion": 99, "entries": [ { "kind": "clip", "source": "lineup", "note": "x" } ] }""";
            string path = Path.Combine(root, ReviewQueue.FileName);
            await File.WriteAllTextAsync(path, newer);

            ReviewQueue queue = new(root);
            await queue.Loaded;

            using (Assert.Multiple())
            {
                await Assert.That(queue.FileProblem).IsNotNull();
                await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(newer);
                await Assert.That(File.Exists(Path.Combine(root, ReviewQueueMigration.BackupFileName))).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // DV_REVIEW_QUEUE names a copy of a real queue; the copy is copied again, so even it is not written.
    [Test]
    public async Task TheOwnersQueue_LosesItsGeneratedLineupClips_AndKeepsTheDossierSection()
    {
        if (Environment.GetEnvironmentVariable("DV_REVIEW_QUEUE") is not { Length: > 0 } source || !File.Exists(source))
        {
            throw new SkipTestException("DV_REVIEW_QUEUE is not set to a copy of a queue");
        }

        string root = Root();
        try
        {
            File.Copy(source, Path.Combine(root, ReviewQueue.FileName));
            ReviewQueue queue = new(root);
            await queue.Loaded;
            Console.WriteLine($"[review-migration] {queue.Migration}; entries left {queue.Entries.Count}");
            using (Assert.Multiple())
            {
                await Assert.That(queue.Migration!.Dropped).IsEqualTo(4792);
                await Assert.That(queue.Migration.Kept).IsEqualTo(0);
                await Assert.That(queue.Migration.CardsDropped).IsEqualTo(11);
                await Assert.That(queue.Clips.Count).IsEqualTo(7);
                await Assert.That(queue.Entries.Count(e => e.Kind == ReviewEntryKind.Section)).IsEqualTo(1);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
