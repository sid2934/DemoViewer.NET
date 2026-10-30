#region

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.ErrorReport;
using DemoViewer.NET.Views.ErrorReport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Report Analysis Errors window body. Three properties are load-bearing and each has a gate here:
///     the diagnostics disclosure is a real two-way binding rather than a one-shot toggle, the footer
///     survives an expanded diagnostics section, and the deliberately disabled Submit button still shows the
///     tooltip that is the only place its reason is written.
/// </summary>
[NotInParallel]
[Category("Render")]
public class ErrorReportViewTests
{
    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static Button ByName(Visual root, string name) => root.GetVisualDescendants().OfType<Button>()
        .First(b => AutomationProperties.GetName(b) == name);

    /// <summary>
    ///     The disclosure drives <see cref="ErrorReportViewModel.IsDiagnosticsExpanded" /> in BOTH directions:
    ///     the window opens collapsed, the user opens it, and a view-model that reloads a report collapsed
    ///     closes it again.
    /// </summary>
    [Test]
    public async Task Disclosure_BindsBothWays_ToIsDiagnosticsExpanded()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            ErrorReportViewModel vm = new();
            ErrorReportView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 680,
                Height = 720,
                Content = view
            };
            window.Show();
            Pump();

            ToggleButton toggle = view.GetVisualDescendants().OfType<ToggleButton>().First();
            ScrollViewer body = view.GetVisualDescendants().OfType<ScrollViewer>()
                .First(s => s.MaxHeight is > 0 and < double.PositiveInfinity);

            bool collapsedAtOpen = body.IsVisible;

            toggle.IsChecked = true;
            Pump();
            bool vmFollowedView = vm.IsDiagnosticsExpanded;
            bool shownAfterToggle = body.IsVisible;

            vm.IsDiagnosticsExpanded = false;
            Pump();
            bool viewFollowedVm = toggle.IsChecked == false && !body.IsVisible;

            using (Assert.Multiple())
            {
                await Assert.That(collapsedAtOpen).IsFalse()
                    .Because("the window opens on the notes, not on a wall of diagnostics");
                await Assert.That(vmFollowedView).IsTrue();
                await Assert.That(shownAfterToggle).IsTrue();
                await Assert.That(viewFollowedVm).IsTrue();
            }
        });
    }

    /// <summary>
    ///     Expanding the diagnostics takes space from the EDITOR, never from the footer. Avalonia's
    ///     DockPanel does not clamp a docked child to the space left, so this measures that the section's
    ///     bottom stays above the footer's top rather than that the footer moved: the footer is docked first
    ///     and never moves, including in the case this is here to catch.
    /// </summary>
    [Test]
    [Arguments(680, 720)] // the design size
    [Arguments(460, 600)] // the recommended window minimum
    public async Task ExpandingDiagnostics_StaysClearOfTheFooter(int w, int h)
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            ErrorReportViewModel vm = new();
            ErrorReportView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = w,
                Height = h,
                Content = view
            };
            window.Show();
            Pump();

            // The footer is the StackPanel holding the action row; the diagnostics the Border wrapping the
            // capped ScrollViewer. Both are found through their children so neither depends on child order.
            Button save = view.GetVisualDescendants().OfType<Button>()
                .First(b => b.Content as string == "Save");
            Control footer = (Control)save.Parent!.Parent!;
            ScrollViewer body = view.GetVisualDescendants().OfType<ScrollViewer>()
                .First(s => s.MaxHeight is > 0 and < double.PositiveInfinity);
            double Top(Control c) => c.TranslatePoint(default, view)!.Value.Y;

            double footerTopCollapsed = Top(footer);

            vm.IsDiagnosticsExpanded = true;
            Pump();

            // The ScrollViewer, not the Border: a DockPanel clamps the Border's arranged height, so the
            // Border always "fits" while its content paints straight over the footer.
            double sectionBottom = Top(body) + body.Bounds.Height;
            double footerTop = Top(footer);
            TextBox notes = view.GetVisualDescendants().OfType<TextBox>().First();

            Console.WriteLine(FormattableString.Invariant(
                $"[er-layout {w}x{h}] sectionBottom={sectionBottom:F1} footerTop={footerTop:F1} notes={notes.Bounds.Height:F1}"));

            using (Assert.Multiple())
            {
                await Assert.That(footerTop).IsEqualTo(footerTopCollapsed).Within(0.5)
                    .Because("the footer is docked first, so opening the section must not move it");
                await Assert.That(sectionBottom).IsLessThanOrEqualTo(footerTop + 0.5)
                    .Because("the section must end above the actions, not draw over them");
                await Assert.That(Top(footer) + footer.Bounds.Height).IsLessThanOrEqualTo(view.Bounds.Height)
                    .Because("the actions stay inside the window");
                await Assert.That(notes.Bounds.Height).IsGreaterThanOrEqualTo(0.0);
            }
        });
    }

    /// <summary>
    ///     Submit For Review is disabled on purpose this round, and a disabled control suppresses tooltips by
    ///     default, so the one sentence explaining why would never be read. The tooltip is opened through the
    ///     real pointer path rather than by setting IsOpen, because ShowOnDisabled is checked on that path and
    ///     nowhere else.
    /// </summary>
    [Test]
    public async Task SubmitForReview_IsDisabled_AndStillShowsItsReason()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            ErrorReportViewModel vm = new();
            ErrorReportView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 680,
                Height = 720,
                Content = view
            };
            window.Show();
            Pump();

            Button submit = ByName(view, "Submit For Review");
            ToolTip.SetShowDelay(submit, 0);

            Point centre = submit.TranslatePoint(new Point(submit.Bounds.Width / 2, submit.Bounds.Height / 2),
                window)!.Value;
            window.MouseMove(centre);
            Pump();
            Pump();

            // Negative control: the same disabled shape WITHOUT the flag, hovered the same way.
            Button bare = new()
            {
                Content = "control",
                IsEnabled = false,
                Width = 80,
                Height = 24
            };
            ToolTip.SetTip(bare, "never shown");
            ToolTip.SetShowDelay(bare, 0);
            Window probe = new()
            {
                Width = 200,
                Height = 100,
                Content = bare
            };
            probe.Show();
            Pump();
            probe.MouseMove(bare.TranslatePoint(new Point(40, 12), probe)!.Value);
            Pump();
            Pump();
            bool control = ToolTip.GetIsOpen(bare);
            probe.Close();

            using (Assert.Multiple())
            {
                await Assert.That(submit.IsEffectivelyEnabled).IsFalse()
                    .Because("submission is not wired, and a live-looking button that does nothing is worse");
                await Assert.That(ToolTip.GetShowOnDisabled(submit)).IsTrue();
                await Assert.That(ToolTip.GetTip(submit) as string).IsEqualTo(
                    "Sends your report and all of the diagnostic information above to the developers for "
                    + "review. Not yet available.");
                await Assert.That(ToolTip.GetIsOpen(submit)).IsTrue()
                    .Because("a disabled control suppresses its tooltip unless ShowOnDisabled is set");
                await Assert.That(control).IsFalse()
                    .Because("without the flag the same pointer path opens nothing, which is what makes "
                             + "the assertion above evidence rather than a tautology");
            }
        });
    }
}
