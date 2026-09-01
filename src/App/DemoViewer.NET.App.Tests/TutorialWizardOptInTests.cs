#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.ViewModels.Setup;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Covers the first-run wizard's opt-in hand-off to the Visual Walkthrough:
///     <see cref="FirstRunWizardViewModel.ShouldStartWalkthrough" />, the flag the composition root reads on
///     <c>Completed</c> to decide whether to launch the tour, is true only when the user reaches the Done
///     page via <see cref="FirstRunWizardViewModel.FinishCommand" /> with the opt-in on. Finishing with it off,
///     or Skipping regardless, leaves it false. Pure VM over a temp-dir <see cref="SettingsService" />, so it
///     runs in parallel.
/// </summary>
public class TutorialWizardOptInTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvtutwizard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Test]
    public async Task Finish_WithOptInOn_RequestsWalkthrough()
    {
        string dir = NewTempDir();
        try
        {
            FirstRunWizardViewModel vm = new(new SettingsService(dir))
            {
                StartWalkthrough = true // the Done-page opt-in (default on)
            };

            vm.FinishCommand.Execute(null);

            await Assert.That(vm.ShouldStartWalkthrough).IsTrue()
                .Because("Finish honours the opt-in — the host starts the tour");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task Finish_WithOptInOff_DoesNotRequestWalkthrough()
    {
        string dir = NewTempDir();
        try
        {
            FirstRunWizardViewModel vm = new(new SettingsService(dir))
            {
                StartWalkthrough = false // user cleared the opt-in on the Done page
            };

            vm.FinishCommand.Execute(null);

            await Assert.That(vm.ShouldStartWalkthrough).IsFalse()
                .Because("Finish with the opt-in cleared must not start the tour");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task Skip_NeverRequestsWalkthrough_RegardlessOfOptIn()
    {
        string dir = NewTempDir();
        try
        {
            FirstRunWizardViewModel vm = new(new SettingsService(dir))
            {
                StartWalkthrough = true // even with the opt-in on, a Skip must not start the tour
            };

            vm.SkipCommand.Execute(null);

            await Assert.That(vm.ShouldStartWalkthrough).IsFalse()
                .Because("a Skip never launches the walkthrough (only Finish honours the opt-in)");
        }
        finally
        {
            Cleanup(dir);
        }
    }
}
