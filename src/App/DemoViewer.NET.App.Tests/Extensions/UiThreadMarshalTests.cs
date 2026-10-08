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
            bool? ranOnUi = null;
            await Task.Run(() => UiThreadMarshal.Run(() => ranOnUi = Dispatcher.UIThread.CheckAccess()));
            for (int i = 0; i < 200 && ranOnUi is null; i++)
            {
                await Task.Delay(5);
            }

            bool inline = false;
            UiThreadMarshal.Run(() => inline = true);

            await Assert.That(ranOnUi).IsEqualTo(true);
            await Assert.That(inline).IsTrue().Because("a raise on the UI thread runs before Run returns");
        });
    }
}
