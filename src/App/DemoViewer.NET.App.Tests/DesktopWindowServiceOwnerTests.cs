#region

using Avalonia.Controls;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.Update;
using DemoViewer.NET.ViewModels.ErrorReport;
using DemoViewer.NET.ViewModels.Update;
using DemoViewer.NET.Views.ErrorReport;
using DemoViewer.NET.Views.Update;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Owner resolution in the REAL <see cref="DesktopWindowService" />. Every other test in this area
///     drives a recording fake, which is exactly why v0.7.1 shipped a launch crash: the composition
///     root ran the "What's new" gate during framework-init, and Avalonia throws
///     "Cannot show window with non-visible owner" from <c>Show(owner)</c> when the main window has
///     been constructed but not yet shown. These pin the service's own contract: an owner is used
///     only when it can legally act as one, and a pop-up shows free-standing rather than throwing
///     otherwise.
/// </summary>
[NotInParallel]
[Category("Render")]
public class DesktopWindowServiceOwnerTests
{
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
    ///     handed to <c>Show(owner)</c>. The pop-up still opens, free-standing.
    /// </summary>
    [Test]
    public async Task UnshownOwner_ShowsFreeStanding_InsteadOfThrowing()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            _opened.Clear();
            TrackWhatsNewWindows();

            // Constructed, never shown, the exact state of MainWindow during framework-init.
            Window owner = new();
            DesktopWindowService service = new(() => owner);

            service.ShowWhatsNew(NewNoticeVm());

            await Assert.That(_opened).Count().IsEqualTo(1);
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

            await Assert.That(_opened).Count().IsEqualTo(1);
            await Assert.That(_opened[0].Owner).IsSameReferenceAs(owner);

            _opened[0].Close();
            owner.Close();
        });
    }

    /// <summary>
    ///     The error-report window is re-used rather than stacked, and the rule for what happens on a second
    ///     open turns on WHICH demo: another demo is another report, so the window re-targets, but the same
    ///     demo must keep the live view-model, which may hold notes the user has typed and not yet saved.
    ///     Pressing the accelerator twice silently discarding them is the failure this pins.
    /// </summary>
    [Test]
    public async Task ReopeningTheErrorReport_KeepsUnsavedNotesOnTheSameDemo_AndRetargetsOnAnother()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            _errorReports.Clear();
            TrackErrorReportWindows();

            Window owner = new();
            owner.Show();
            DesktopWindowService service = new(() => owner);

            ErrorReportViewModel first = ReportVm("/demos/same.dem");
            service.ShowErrorReport(first);
            first.Notes = "half-written, never saved";

            service.ShowErrorReport(ReportVm("/demos/same.dem"));
            ErrorReportViewModel afterSame = (ErrorReportViewModel)_errorReports[0].DataContext!;

            service.ShowErrorReport(ReportVm("/demos/another.dem"));
            ErrorReportViewModel afterOther = (ErrorReportViewModel)_errorReports[0].DataContext!;

            using (Assert.Multiple())
            {
                await Assert.That(_errorReports).Count().IsEqualTo(1)
                    .Because("one window, re-used, not a second report on screen with nothing to tell them "
                             + "apart");
                await Assert.That(afterSame).IsSameReferenceAs(first);
                await Assert.That(afterSame.Notes).IsEqualTo("half-written, never saved");
                await Assert.That(afterOther.DemoPath).IsEqualTo("/demos/another.dem");
            }

            _errorReports[0].Close();
            owner.Close();
        });
    }

    private static ErrorReportViewModel ReportVm(string demoPath) =>
        new(demoPath, null, null, () => (null, null));

    private static readonly List<ErrorReportWindow> _errorReports = [];
    private static bool _errorTrackingInstalled;

    private static void TrackErrorReportWindows()
    {
        if (_errorTrackingInstalled)
        {
            return;
        }

        _errorTrackingInstalled = true;
        Window.WindowOpenedEvent.AddClassHandler(
            typeof(ErrorReportWindow),
            (sender, _) => _errorReports.Add((ErrorReportWindow)sender!));
    }

    /// <summary>Returns no notes, so opening the window never touches the network.</summary>
    private sealed class SilentNotesService : IReleaseNotesService
    {
        public Task<ReleaseNotes?> GetForVersionAsync(string version, CancellationToken ct = default) =>
            Task.FromResult<ReleaseNotes?>(null);
    }
}
