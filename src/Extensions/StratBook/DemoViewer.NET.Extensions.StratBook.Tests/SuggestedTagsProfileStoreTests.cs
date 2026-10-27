#region

using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The profile file: <c>&lt;config&gt;/suggested-tags/profile.json</c>,
///     seeded with the shipped default on first read, session-only with a null folder (the browser).
/// </summary>
public class SuggestedTagsProfileStoreTests
{
    [Test]
    public async Task TheStore_KeepsTheProfileInMemory_WithNoFolder()
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
        string root = Path.Combine(Path.GetTempPath(), "dv-stp-" + Guid.NewGuid().ToString("N"));
        try
        {
            IExtensionFolder folder = PackStores.Folder(root, StratBookStores.SuggestedTags);
            ProfileStore store = new(folder);
            string file = Path.Combine(folder.Path, "profile.json");

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
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task Save_PersistsAndIsReadBackByAFreshStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-stp-" + Guid.NewGuid().ToString("N"));
        try
        {
            IExtensionFolder folder = PackStores.Folder(root, StratBookStores.SuggestedTags);
            ProfileStore store = new(folder);
            DetectorProfile edited = DetectorProfile.Default.With("opener", "K", 5);
            store.Save(edited);

            ProfileStore reopened = new(folder);
            await Assert.That(reopened.Current.Get("opener", "K")).IsEqualTo(5.0);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
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
        string root = Path.Combine(Path.GetTempPath(), "dv-stp-" + Guid.NewGuid().ToString("N"));
        try
        {
            IExtensionFolder folder = PackStores.Folder(root, StratBookStores.SuggestedTags);
            Directory.CreateDirectory(folder.Path);
            File.WriteAllText(Path.Combine(folder.Path, "profile.json"), "not json");

            ProfileStore store = new(folder);

            await Assert.That(store.Current.ToJson()).IsEqualTo(DetectorProfile.Default.ToJson());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
