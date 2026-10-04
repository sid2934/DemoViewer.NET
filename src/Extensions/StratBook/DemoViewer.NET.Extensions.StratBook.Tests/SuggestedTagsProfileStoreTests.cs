#region

using DemoViewer.NET.Modules.SuggestedTags;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The profile file (suggested-tags.md §3.7, step 6): <c>&lt;config&gt;/suggested-tags/profile.json</c>,
///     seeded with the shipped default on first read, session-only with a null directory (the browser).
/// </summary>
public class SuggestedTagsProfileStoreTests
{
    [Test]
    public async Task TheStore_KeepsTheProfileInMemory_WithNoDirectory()
    {
        ProfileStore store = new(null);

        await Assert.That(store.IsPersistent).IsFalse();
        await Assert.That(store.Current.ToJson()).IsEqualTo(DetectorProfile.Default.ToJson())
            .Because("nothing was ever saved, so Current is the shipped default");

        DetectorProfile edited = DetectorProfile.Default.With("execute", "N", 3);
        store.Save(edited);

        await Assert.That(store.Current.Get("execute", "N")).IsEqualTo(3.0);
    }

    [Test]
    public async Task FirstRead_SeedsTheFileWithTheShippedDefault()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dv-stp-" + Guid.NewGuid().ToString("N"));
        try
        {
            ProfileStore store = new(directory);
            string file = Path.Combine(directory, "profile.json");

            await Assert.That(File.Exists(file)).IsFalse().Because("nothing has read Current yet");
            DetectorProfile current = store.Current;

            using (Assert.Multiple())
            {
                await Assert.That(current.ToJson()).IsEqualTo(DetectorProfile.Default.ToJson());
                await Assert.That(File.Exists(file)).IsTrue().Because("the first read seeds the file, the way a theme drop-in folder is seeded");
                await Assert.That(DetectorProfile.Parse(File.ReadAllText(file)).ToJson()).IsEqualTo(DetectorProfile.Default.ToJson());
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Test]
    public async Task Save_PersistsAndIsReadBackByAFreshStore()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dv-stp-" + Guid.NewGuid().ToString("N"));
        try
        {
            ProfileStore store = new(directory);
            DetectorProfile edited = DetectorProfile.Default.With("opener", "K", 5);
            store.Save(edited);

            ProfileStore reopened = new(directory);
            await Assert.That(reopened.Current.Get("opener", "K")).IsEqualTo(5.0);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Test]
    public async Task Save_RaisesChanged_AndUpdatesCurrentWithoutAReload()
    {
        ProfileStore store = new(null);
        int changed = 0;
        store.Changed += () => changed++;

        DetectorProfile edited = DetectorProfile.Default.With("retake", "minGroup", 3);
        store.Save(edited);

        using (Assert.Multiple())
        {
            await Assert.That(changed).IsEqualTo(1);
            await Assert.That(store.Current.Get("retake", "minGroup")).IsEqualTo(3.0);
        }
    }

    [Test]
    public async Task ABadHandEditedFile_FallsBackToTheShippedDefault()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dv-stp-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "profile.json"), "not json");

            ProfileStore store = new(directory);

            await Assert.That(store.Current.ToJson()).IsEqualTo(DetectorProfile.Default.ToJson());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
