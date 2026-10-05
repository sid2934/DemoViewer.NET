#region

using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The backstops on a headless UI thread, with this test assembly standing in for an extension: a click
///     handler that throws through the dispatcher is caught and counted, a binding getter that throws leaves the
///     view running, a binding error is logged against the extension and never counted, and a view whose
///     constructor throws inside layout is replaced by the placeholder.
/// </summary>
[NotInParallel]
[Category("Render")]
public class ExtensionBackstopTests
{
    [Test]
    public async Task AClickHandlerThatThrows_IsCaughtOnTheDispatcher_AndCounted() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FaultRig rig = new();
            using IDisposable backstops = ExtensionBackstops.Install(rig.Faults);
            Button button = new();
            button.Click += (_, _) => throw new InvalidOperationException("click");
            Window window = new() { Content = button };
            window.Show();
            try
            {
                Dispatcher.UIThread.Post(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                Dispatcher.UIThread.RunJobs();

                ExtensionFaultState state = rig.Faults.StateOf("pack.fake");
                using (Assert.Multiple())
                {
                    await Assert.That(state.Count).IsEqualTo(1);
                    await Assert.That(state.LastSite).IsEqualTo("UI thread");
                }
            }
            finally
            {
                window.Close();
            }
        });

    // Avalonia's binding engine catches a getter's throw and logs nothing for it: the view keeps running and
    // the fault is not counted.
    [Test]
    public async Task ABindingGetterThatThrows_LeavesTheViewRunning_AndIsNotCounted() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FaultRig rig = new();
            using IDisposable backstops = ExtensionBackstops.Install(rig.Faults);
            TextBlock text = new() { DataContext = new ThrowingGetter() };
            text.Bind(TextBlock.TextProperty, new Binding(nameof(ThrowingGetter.Value)));
            Window window = new() { Content = text };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();

                using (Assert.Multiple())
                {
                    await Assert.That(text.Text).IsNull().Or.IsEmpty();
                    await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(0);
                }
            }
            finally
            {
                window.Close();
            }
        });

    // A binding error Avalonia does report (here a path the view model lacks) is logged against the extension
    // whose view model it is, and never counted.
    [Test]
    public async Task ABindingErrorInAnExtensionsView_IsLoggedAgainstTheExtension_AndNeverCounted() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using CapturedLogs logs = CapturedLogs.Install();
            FaultRig rig = new();
            using IDisposable backstops = ExtensionBackstops.Install(rig.Faults);
            TextBlock text = new() { DataContext = new ThrowingGetter() };
            text.Bind(TextBlock.TextProperty, new Binding("Missing"));
            Window window = new() { Content = text };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();

                using (Assert.Multiple())
                {
                    await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(0);
                    await Assert.That(logs.WithEvent(22).Any(m => m.Contains("Extension Fake failed in binding", StringComparison.Ordinal)))
                        .IsTrue();
                }
            }
            finally
            {
                window.Close();
            }
        });

    [Test]
    public async Task AViewWhoseConstructorThrowsInsideLayout_IsReplacedByThePlaceholder() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            FaultRig rig = new();
            ContentControl host = new()
            {
                Content = new object(),
                ContentTemplate = new FuncDataTemplate<object>((_, _) => ViewLocator.Create(typeof(ThrowingView), rig.Faults))
            };
            Window window = new() { Content = host };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                TextBlock? shown = host.Presenter?.Child as TextBlock;

                using (Assert.Multiple())
                {
                    await Assert.That(shown?.Text).Contains("could not show this view");
                    await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(1);
                }
            }
            finally
            {
                window.Close();
            }
        });

    private sealed class ThrowingGetter
    {
        private readonly string _message = "getter";

        public string Value => throw new InvalidOperationException(_message);
    }

    private sealed class ThrowingView : UserControl
    {
        public ThrowingView() => throw new InvalidOperationException("view ctor");
    }
}
