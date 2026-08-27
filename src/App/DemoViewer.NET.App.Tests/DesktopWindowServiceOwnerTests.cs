#region

using Avalonia.Controls;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.Update;
using DemoViewer.NET.ViewModels.Update;
using DemoViewer.NET.Views.Update;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Owner resolution in the REAL <see cref="DesktopWindowService" />. Every other test in this area
///     drives a recording fake, which is exactly why v0.7.1 shipped a launch crash: the composition
///     root ran the "What's new" gate during framework-init, and Avalonia throws
///     "Cannot show window with non-visible owner" from <c>Show(owner)</c> when the main window has
///     been constructed but not yet shown. These pin the service's own contract — an owner is used
///     only when it can legally act as one, and a pop-up shows free-standing rather than throwing
///     otherwise.
/// </summary>
[NotInParallel]
[Category("Render")]
public class DesktopWindowServiceOwnerTests
{
    /// <summary>Returns no notes, so opening the window never touches the network.</summary>
    private sealed class SilentNotesService : IReleaseNotesService
    {
        public Task<ReleaseNotes?> GetForVersionAsync(string version, CancellationToken ct = default) =>
            Task.FromResult<ReleaseNotes?>(null);
    }

    // Class handlers cannot be removed, so this is installed once for the process and the capture
    // list is cleared per case.
    private static readonly List<WhatsNewWindow> _opened = [];
    private static bool _trackingInstalled;

    private static void TrackWhatsNewWindows()
    {
        if (_trackingInstalled)
        {
            return;
        }

        _trackingInstalled = true;
        Window.WindowOpenedEvent.AddClassHandler(
            typeof(WhatsNewWindow),
            (sender, _) => _opened.Add((WhatsNewWindow)sender!));
    }

    private static WhatsNewViewModel NewNoticeVm() => new("9.9.9", new SilentNotesService());

    /// <summary>
    ///     The v0.7.1 crash, inverted: an owner that exists but has never been shown must not be
    ///     handed to <c>Show(owner)</c>. The pop-up still opens — free-standing.
    /// </summary>
    [Test]
    public async Task UnshownOwner_ShowsFreeStanding_InsteadOfThrowing()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            _opened.Clear();
            TrackWhatsNewWindows();

            // Constructed, never shown — the exact state of MainWindow during framework-init.
            Window owner = new();
            DesktopWindowService service = new(() => owner);

            service.ShowWhatsNew(NewNoticeVm());

            await Assert.That(_opened).HasCount().EqualTo(1);
            await Assert.That(_opened[0].Owner)
                .IsNull()
                .Because("a non-visible owner is not a legal owner — the pop-up must stand alone");

            _opened[0].Close();
            owner.Close();
        });
    }

    /// <summary>A shown owner is still used, so the pop-up keeps its normal parenting.</summary>
    [Test]
    public async Task ShownOwner_IsUsedAsOwner()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            _opened.Clear();
            TrackWhatsNewWindows();

            Window owner = new();
            owner.Show();
            DesktopWindowService service = new(() => owner);

            service.ShowWhatsNew(NewNoticeVm());

            await Assert.That(_opened).HasCount().EqualTo(1);
            await Assert.That(_opened[0].Owner).IsSameReferenceAs(owner);

            _opened[0].Close();
            owner.Close();
        });
    }
}
