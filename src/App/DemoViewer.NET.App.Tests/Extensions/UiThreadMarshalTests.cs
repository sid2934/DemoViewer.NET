#region

using Avalonia.Threading;
using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The marshal the extension decorators hand host handlers to: inline on the UI thread, posted to it
///     from anywhere else.
/// </summary>
[NotInParallel]
public class UiThreadMarshalTests
{
    [Test]
    public async Task FromAWorker_TheActionRunsOnTheUiThread_AndInlineFromTheUiThreadItself()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            TaskCompletionSource<bool> ran = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await Task.Run(() => UiThreadMarshal.Run(() => ran.TrySetResult(Dispatcher.UIThread.CheckAccess())));
            bool ranOnUi = await ran.Task.WaitAsync(TimeSpan.FromSeconds(30));

            bool inline = false;
            UiThreadMarshal.Run(() => inline = true);

            await Assert.That(ranOnUi).IsTrue();
            await Assert.That(inline).IsTrue().Because("a raise on the UI thread runs before Run returns");
        });
    }
}
